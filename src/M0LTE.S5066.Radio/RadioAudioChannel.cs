using System.Collections.Concurrent;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Station;

namespace M0LTE.S5066.Radio;

/// <summary>
/// The modem's 9600 Hz audio path over a sound card and a PTT line (core-architecture.md 12.11):
/// received audio is read from the capture device and decimated; each burst is upsampled, then
/// sent keyed, with TXDELAY of silence before it and the TX tail after it, and the optional
/// identification between TXDELAY and the burst.
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
/// <item>The radio is never left keyed by this class: it is unkeyed on every way out of a keyup,
/// whatever is thrown; a transmit guard on a timer unkeys it if a keyup is still keyed
/// <see cref="GuardMargin"/> after its own length (a hung playback device) and stops the channel;
/// <see cref="Dispose"/> unkeys first, before it waits for or closes anything; and
/// <see cref="EmergencyUnkey"/> is there for a process going down without Dispose. A keyup longer
/// than any burst the node can plan (<see cref="MaxBurstMs"/>) is never keyed.</item>
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
    private readonly BlockingCollection<(float[] Samples, int IdSamples, Action Played)> _bursts = new();
    private readonly object _rxGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeSpan _guardMargin;
    private readonly object _pttGate = new();
    private bool _pttKeyed;
    private bool _pttShut;
    private int _keyGeneration;
    private volatile bool _guardTripped;
    private int _disposed;
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
        : this(input, output, ptt, txDelayMs, txTailMs, log, GuardMargin)
    {
    }

    /// <summary>Creates the channel with another transmit guard margin, for tests.</summary>
    internal RadioAudioChannel(IAudioInput input, IAudioOutput output, IPttControl ptt, int txDelayMs, int txTailMs, Action<string>? log, TimeSpan guardMargin)
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
        _guardMargin = guardMargin;
        _decimator = new Decimator(_rate, _factor);
    }

    /// <summary>
    /// The longest burst, in ms of modem audio, that is ever keyed: the core's cap on a
    /// transmission (127.5 s, C-0106), which also covers the longest single D_PDU at the slowest
    /// waveform (about 116 s), plus 2.5 s for rounding. TXDELAY and the tail come on top.
    /// </summary>
    public const int MaxBurstMs = DtsConfig.MaxTransmissionLimitMs + 2500;

    /// <summary>
    /// How long past its own length a keyup may still be keyed before the transmit guard unkeys
    /// the radio: the playback buffer (120 ms) drained, with room for a loaded Pi.
    /// </summary>
    public static readonly TimeSpan GuardMargin = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The playback device's count of underruns so far, or null when it keeps none. Each one is a
    /// gap in a burst on air, so a burst that had any is logged; on a busy Pi this is the sign.
    /// </summary>
    internal Func<int>? PlaybackXruns { get; init; }

    /// <summary>The capture device's count of overruns so far, or null when it keeps none; each is logged.</summary>
    internal Func<int>? CaptureXruns { get; init; }

    /// <summary>
    /// The identification hook (plan 9.1, decision D4; core-architecture.md 10 item 13), or null for
    /// none: asked once per keyup, as the burst is queued, for audio at the card's rate to send
    /// after TXDELAY and before the burst, or null to send none this time. At most
    /// <see cref="CwIdSettings.MaxDurationMs"/> long.
    /// </summary>
    public Func<float[]?>? Identification { get; set; }

    /// <summary>Silence between the identification and the burst, in ms, so the two are heard apart.</summary>
    public const int IdentificationGapMs = 300;

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
        float[] keyup = Keyup(samples, out int idSamples);
        try
        {
            _bursts.Add((keyup, idSamples, played));
        }
        catch (InvalidOperationException)
        {
            // Stopping: nothing more goes out, but the burst still ends.
            played();
        }
    }

    /// <summary>
    /// The whole keyup at the card's rate: TXDELAY of silence, the identification and a short gap
    /// when <see cref="Identification"/> gives one, the burst upsampled, then the tail of silence.
    /// </summary>
    internal float[] Keyup(float[] samples) => Keyup(samples, out _);

    /// <summary>The keyup, and how many of its samples are the identification and its gap.</summary>
    private float[] Keyup(float[] samples, out int idSamples)
    {
        var upsampler = new Upsampler(_rate, _factor);
        int lead = (int)((long)_rate * _txDelayMs / 1000);
        float[]? id = Identification?.Invoke();
        idSamples = 0;
        if (id is { Length: > 0 })
        {
            idSamples = id.Length + (int)((long)_rate * IdentificationGapMs / 1000);
            lead += idSamples;
        }

        int tail = (int)((long)_rate * _txTailMs / 1000);
        int body = upsampler.OutputLength(samples.Length);
        var keyup = new float[lead + body + tail];
        id?.CopyTo(keyup, (int)((long)_rate * _txDelayMs / 1000));
        upsampler.Process(samples, keyup.AsSpan(lead, body));
        return keyup;
    }

    /// <summary>
    /// Unkeys the radio and keeps it unkeyed, then stops: a burst in progress is cut short within
    /// 100 ms, and the devices are closed.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // First, before waiting for or closing anything: whatever happens below, the radio is off.
        EmergencyUnkey();
        _stop.Cancel();
        _bursts.CompleteAdding();
        bool transmitEnded = _transmit?.Join(TimeSpan.FromSeconds(5)) ?? true;
        (_input as IDisposable)?.Dispose();
        _capture?.Join(TimeSpan.FromSeconds(2));
        if (transmitEnded)
        {
            (_output as IDisposable)?.Dispose();
        }
        else
        {
            // Closing the device under a write that never returns could crash the process; the
            // radio is already unkeyed, and the process is going.
            _log?.Invoke("radio: the playback device did not stop; left open");
        }

        (_ptt as IDisposable)?.Dispose();
    }

    private void CaptureLoop()
    {
        // 20 ms reads: the decimator's output reaches the modem and the busy detector promptly.
        var raw = new float[_rate / 50];
        var decimated = new float[_decimator.MaxOutput(raw.Length)];
        int overrunsSeen = 0;
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

                if (CaptureXruns?.Invoke() is int overruns && overruns != overrunsSeen)
                {
                    overrunsSeen = overruns;
                    _log?.Invoke($"radio: the capture device overran ({overruns} so far), so some received audio was lost; the Pi is too busy");
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
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Closed under the read at shutdown.
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Fail($"the capture device stopped: {e.Message}", e);
        }
        catch (Exception e)
        {
            // Anything else on this thread (the demodulator, the busy detector, the recording)
            // stops the channel rather than the process, which could leave a burst keyed.
            Fail($"the receive path stopped: {e.Message}", e);
        }
    }

    private void TransmitLoop()
    {
        try
        {
            foreach ((float[] keyup, int idSamples, Action played) in _bursts.GetConsumingEnumerable())
            {
                bool carryOn = Send(keyup, idSamples, out Exception? failure);

                // TransmitDone reaches the modem between two receive callbacks, as IAudioChannel asks.
                lock (_rxGate)
                {
                    played();
                }

                if (!carryOn)
                {
                    Fail(failure!.Message, failure);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            // Anything unexpected on this thread (a handler, a bug) stops the channel rather than
            // the process, with the radio unkeyed.
            UnkeyPtt();
            Fail($"the transmit path stopped: {e.Message}", e);
        }
    }

    /// <summary>
    /// Sends one keyup: key, write, drain, unkey. The radio is unkeyed on every way out, and the
    /// transmit guard unkeys it from another thread if the keyup overruns. False when the channel
    /// must stop, with the reason.
    /// </summary>
    private bool Send(float[] keyup, int idSamples, out Exception? failure)
    {
        failure = null;
        if (_stop.IsCancellationRequested)
        {
            // Stopping: a burst still queued is never keyed.
            return true;
        }

        // The identification is checked where it is made (CwIdSettings.MaxDurationMs), so it is on top.
        long longest = ((long)_rate * (MaxBurstMs + _txDelayMs + _txTailMs) / 1000) + idSamples;
        if (keyup.Length > longest)
        {
            _log?.Invoke($"radio: burst not sent, {keyup.Length / (_rate / 1000)} ms is longer than any burst the node can plan ({MaxBurstMs} ms plus txdelay and tail)");
            return true;
        }

        bool keyed = false;
        try
        {
            Keyed?.Invoke(true);
            int generation;
            try
            {
                keyed = KeyPtt(out generation);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Unkeyed anyway, in case the write half happened.
                UnkeyPtt();
                _log?.Invoke($"radio: burst not sent, PTT failed: {e.Message}");
                return true;
            }

            if (!keyed)
            {
                // The PTT has been shut off (stopping, or the guard tripped).
                return !_guardTripped;
            }

            int underrunsBefore = PlaybackXruns?.Invoke() ?? 0;
            TimeSpan allowed = TimeSpan.FromSeconds(keyup.Length / (double)_rate) + _guardMargin;
            using (var guard = new Timer(_ => Trip(generation, allowed), null, allowed, Timeout.InfiniteTimeSpan))
            {
                // In 100 ms pieces, so a stop cuts a long burst short at once rather than closing
                // the device under a write in progress.
                int piece = _rate / 10;
                for (int at = 0; at < keyup.Length && !_stop.IsCancellationRequested && !_guardTripped; at += piece)
                {
                    _output.Write(keyup.AsSpan(at, Math.Min(piece, keyup.Length - at)));
                }

                if (!_stop.IsCancellationRequested && !_guardTripped)
                {
                    _output.Drain();
                }
            }

            if (PlaybackXruns?.Invoke() - underrunsBefore is > 0 and int underruns)
            {
                _log?.Invoke($"radio: the playback device underran {underruns} time(s) during the burst, so it went out with gaps; the Pi is too busy");
            }

            if (_guardTripped)
            {
                failure = new TimeoutException($"transmit guard: the radio was still keyed {allowed.TotalSeconds:0.#} s after the key, so it was unkeyed");
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (_stop.IsCancellationRequested)
            {
                return true;
            }

            _log?.Invoke($"radio: burst cut short, the playback device failed: {e.Message}");
            failure = new IOException($"the playback device stopped: {e.Message}", e);
            return false;
        }
        finally
        {
            // On every way out, whatever was thrown: a CM108 holds its pin where it was last set.
            if (keyed)
            {
                UnkeyPtt();
            }

            Keyed?.Invoke(false);
        }
    }

    /// <summary>Keys the radio unless the PTT has been shut off; the generation names this keyup for the guard.</summary>
    private bool KeyPtt(out int generation)
    {
        lock (_pttGate)
        {
            generation = ++_keyGeneration;
            if (_pttShut)
            {
                return false;
            }

            _pttKeyed = true;
            _ptt.Key();
            return true;
        }
    }

    private void UnkeyPtt()
    {
        lock (_pttGate)
        {
            _pttKeyed = false;
            TryUnkey();
        }
    }

    /// <summary>Under the PTT lock: unkeys, logging a failure rather than throwing.</summary>
    private void TryUnkey()
    {
        try
        {
            _ptt.Unkey();
        }
        catch (Exception e)
        {
            _log?.Invoke($"radio: unkey failed: {e.Message}");
        }
    }

    /// <summary>
    /// The transmit guard, on a timer thread: if the keyup it was set for is still keyed, unkey
    /// and shut the PTT off for good, whatever the transmit thread is stuck in.
    /// </summary>
    private void Trip(int generation, TimeSpan allowed)
    {
        lock (_pttGate)
        {
            if (!_pttKeyed || generation != _keyGeneration)
            {
                return;
            }

            _guardTripped = true;
            _pttShut = true;
            _pttKeyed = false;
            TryUnkey();
        }

        var e = new TimeoutException($"transmit guard: the radio was still keyed {allowed.TotalSeconds:0.#} s after the key, so it was unkeyed");
        Fail(e.Message, e);
    }

    /// <summary>
    /// Unkeys the radio and keeps it unkeyed, from any thread, at once: for a process that is
    /// going down (an unhandled exception, an exit) without the orderly <see cref="Dispose"/>.
    /// Never throws.
    /// </summary>
    public void EmergencyUnkey()
    {
        try
        {
            lock (_pttGate)
            {
                _pttShut = true;
                _pttKeyed = false;
                _ptt.Unkey();
            }
        }
        catch
        {
            // Last resort: nothing more can be done from here.
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
