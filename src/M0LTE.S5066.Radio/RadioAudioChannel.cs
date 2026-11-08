using System.Collections.Concurrent;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Host.Audio;

namespace M0LTE.S5066.Radio;

/// <summary>
/// The modem's 9600 Hz audio path over a sound card and a PTT line (core-architecture.md 12.11):
/// received audio is read from the capture device and decimated; each burst is upsampled, then
/// sent keyed, with TXDELAY of silence before it and the TX tail after it.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The 110D waveform sits at the same audio frequencies either side of the rate change, so this
/// is a resampler and nothing else: 48000 is 5 x 9600.</item>
/// <item>A burst is upsampled in <see cref="Play"/>, on the modem's worker thread, before the radio
/// is keyed: a long burst's filtering would otherwise be dead air after the key, on top of TXDELAY.
/// A fresh upsampler per burst, so no filter state from one burst leaks into the next.</item>
/// <item>TXDELAY is silence after the key, because the 110D preamble starts at its first sample
/// and a radio still switching to transmit would clip it. The tail is silence written before the
/// output is drained, so the radio is unkeyed only once the tail has played.</item>
/// <item><see cref="Keyed"/> is raised before the key and after the unkey, so a busy detector
/// can leave out our own transmission.</item>
/// </list>
/// </remarks>
public sealed class RadioAudioChannel : IAudioChannel, IDisposable
{
    private readonly IAudioInput _input;
    private readonly IAudioOutput _output;
    private readonly IPttControl _ptt;
    private readonly int _rate;
    private readonly int _factor;
    private readonly int _txDelayMs;
    private readonly int _txTailMs;
    private readonly Action<string>? _log;
    private readonly Decimator _decimator;
    private readonly BlockingCollection<(float[] Samples, Action Played)> _bursts = new();
    private readonly object _rxGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Action<ReadOnlyMemory<float>>? _received;
    private Thread? _capture;
    private Thread? _transmit;

    /// <summary>Creates the channel; nothing is read, played or keyed until <see cref="Start"/>.</summary>
    /// <param name="input">The capture device.</param>
    /// <param name="output">The playback device, at the capture device's rate.</param>
    /// <param name="ptt">The PTT line.</param>
    /// <param name="txDelayMs">Silence after the key, before the burst.</param>
    /// <param name="txTailMs">Silence after the burst, before the unkey.</param>
    /// <param name="log">A sink for one-line log messages, or null.</param>
    public RadioAudioChannel(IAudioInput input, IAudioOutput output, IPttControl ptt, int txDelayMs, int txTailMs, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(ptt);
        ArgumentOutOfRangeException.ThrowIfNegative(txDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(txTailMs);
        if (input.SampleRate != output.SampleRate)
        {
            throw new ArgumentException($"the capture device runs at {input.SampleRate} Hz and the playback device at {output.SampleRate} Hz; they must match", nameof(output));
        }

        if (input.SampleRate % IAudioChannel.SampleRate != 0)
        {
            throw new ArgumentException($"the sound card runs at {input.SampleRate} Hz, which is not a multiple of the modem's {IAudioChannel.SampleRate} Hz", nameof(input));
        }

        _input = input;
        _output = output;
        _ptt = ptt;
        _rate = input.SampleRate;
        _factor = _rate / IAudioChannel.SampleRate;
        _txDelayMs = txDelayMs;
        _txTailMs = txTailMs;
        _log = log;
        _decimator = new Decimator(_rate, _factor);
    }

    /// <summary>Raised with true just before the radio is keyed and with false just after it is unkeyed, on the transmit thread.</summary>
    public event Action<bool>? Keyed;

    /// <summary>Each chunk of received audio at 9600 Hz, on the capture thread, just before the modem gets it. The chunk is only valid during the call.</summary>
    public event Action<ReadOnlyMemory<float>>? Heard;

    /// <summary>Raised once, on the thread that failed, if the capture or playback device stops working; the channel does nothing more after it.</summary>
    public event Action<Exception>? Failed;

    /// <inheritdoc/>
    public void Start(Action<ReadOnlyMemory<float>> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        _received = received;
        _capture = new Thread(CaptureLoop) { IsBackground = true, Name = "radio capture" };
        _transmit = new Thread(TransmitLoop) { IsBackground = true, Name = "radio transmit" };
        _capture.Start();
        _transmit.Start();
    }

    /// <inheritdoc/>
    public void Play(float[] samples, Action played)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(played);
        float[] keyup = Keyup(samples);
        try
        {
            _bursts.Add((keyup, played));
        }
        catch (InvalidOperationException)
        {
            // Stopping: nothing more goes out, but the burst still ends.
            played();
        }
    }

    /// <summary>
    /// The whole keyup at the card's rate: TXDELAY of silence, the burst upsampled, then the
    /// tail of silence.
    /// </summary>
    internal float[] Keyup(float[] samples)
    {
        var upsampler = new Upsampler(_rate, _factor);
        int lead = (int)((long)_rate * _txDelayMs / 1000);
        int tail = (int)((long)_rate * _txTailMs / 1000);
        int body = upsampler.OutputLength(samples.Length);
        var keyup = new float[lead + body + tail];
        upsampler.Process(samples, keyup.AsSpan(lead, body));
        return keyup;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stop.Cancel();
        _bursts.CompleteAdding();
        _transmit?.Join(TimeSpan.FromSeconds(5));
        (_input as IDisposable)?.Dispose();
        _capture?.Join(TimeSpan.FromSeconds(2));
        (_output as IDisposable)?.Dispose();
        (_ptt as IDisposable)?.Dispose();
        _stop.Dispose();
    }

    private void CaptureLoop()
    {
        // 20 ms reads: the decimator's output reaches the modem and the busy detector promptly.
        var raw = new float[_rate / 50];
        var decimated = new float[_decimator.MaxOutput(raw.Length)];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int n = _input.Read(raw);
                if (n <= 0)
                {
                    if (_stop.IsCancellationRequested)
                    {
                        return;
                    }

                    throw new IOException("the capture device closed");
                }

                int produced = _decimator.Process(raw.AsSpan(0, n), decimated);
                if (produced == 0)
                {
                    continue;
                }

                ReadOnlyMemory<float> chunk = decimated.AsMemory(0, produced);
                lock (_rxGate)
                {
                    Heard?.Invoke(chunk);
                    _received?.Invoke(chunk);
                }
            }
        }
        catch (Exception e) when (!_stop.IsCancellationRequested && e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Fail($"the capture device stopped: {e.Message}", e);
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Closed under the read at shutdown.
        }
    }

    private void TransmitLoop()
    {
        foreach ((float[] keyup, Action played) in _bursts.GetConsumingEnumerable())
        {
            bool keyed = false;
            try
            {
                Keyed?.Invoke(true);
                _ptt.Key();
                keyed = true;
                _output.Write(keyup);
                _output.Drain();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested)
                {
                    _log?.Invoke(keyed ? $"radio: burst cut short, the playback device failed: {e.Message}" : $"radio: burst not sent, PTT failed: {e.Message}");
                }

                if (keyed && !_stop.IsCancellationRequested)
                {
                    Unkey();
                    Keyed?.Invoke(false);
                    lock (_rxGate)
                    {
                        played();
                    }

                    Fail($"the playback device stopped: {e.Message}", e);
                    return;
                }
            }

            if (keyed)
            {
                Unkey();
            }

            Keyed?.Invoke(false);

            // TransmitDone reaches the modem between two receive callbacks, as IAudioChannel asks.
            lock (_rxGate)
            {
                played();
            }
        }
    }

    private void Unkey()
    {
        try
        {
            _ptt.Unkey();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _log?.Invoke($"radio: unkey failed: {e.Message}");
        }
    }

    private int _failed;

    private void Fail(string message, Exception e)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        _log?.Invoke("radio: " + message);
        Failed?.Invoke(e);
    }
}
