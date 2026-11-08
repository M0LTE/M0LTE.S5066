using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Radio;

/// <summary>
/// The modem on a radio: every call and event passes straight through to the inner modem (the
/// M0LTE.Ms110d adapter), with two additions. When a <see cref="RadioBusy"/> is given, its answer
/// is the node's external busy input (<see cref="IModemEvents.ChannelBusy"/>). And each burst sent
/// or heard is one log line (core-architecture.md 12.11).
/// </summary>
public sealed class RadioHostModem : IHostModem, IHostModemWrapper
{
    private readonly IHostModem _inner;
    private readonly RadioBusy? _busy;
    private readonly Action<string>? _log;
    private string? _sending;

    /// <summary>Creates the modem.</summary>
    /// <param name="inner">The modem.</param>
    /// <param name="busy">The busy detector, or null to raise no busy input.</param>
    /// <param name="log">A sink for the per-burst lines, or null.</param>
    public RadioHostModem(IHostModem inner, RadioBusy? busy, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _busy = busy;
        _log = log;
    }

    /// <inheritdoc/>
    public IHostModem Inner => _inner;

    /// <inheritdoc/>
    public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) => _inner.PlanBurst(wn, interleaver, byteCount);

    /// <inheritdoc/>
    public void Start(IModemEvents events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var wrapped = new Events(this, events);
        if (_busy is not null)
        {
            _busy.Changed += events.ChannelBusy;
        }

        _inner.Start(wrapped);
    }

    /// <inheritdoc/>
    public void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom)
    {
        // Set first: the burst may be out (TransmitDone) before Transmit returns.
        Volatile.Write(ref _sending, $"radio: tx {Waveform(wn, interleaver)} {bytes.Length} bytes");
        try
        {
            _inner.Transmit(bytes, wn, interleaver, k, eom);
        }
        catch
        {
            Volatile.Write(ref _sending, null);
            throw;
        }
    }

    /// <inheritdoc/>
    public void TerminateReceive() => _inner.TerminateReceive();

    /// <inheritdoc/>
    public void SetMaxInputBlocks(int n) => _inner.SetMaxInputBlocks(n);

    /// <summary>The waveform as a log line shows it, for example <c>wn6 short</c>.</summary>
    internal static string Waveform(int wn, Interleaver interleaver) => $"wn{wn} {InterleaverNames.ToName(interleaver).Replace('_', ' ')}";

    private sealed class Events(RadioHostModem owner, IModemEvents inner) : IModemEvents
    {
        private int _bytes;

        public void BurstStarted(int? blockMs)
        {
            _bytes = 0;
            inner.BurstStarted(blockMs);
        }

        public void BytesReceived(ReadOnlyMemory<byte> bytes)
        {
            _bytes += bytes.Length;
            inner.BytesReceived(bytes);
        }

        public void BurstEnded(BurstEndReason reason, int wn, Interleaver il, int k, int? snrTenthDb, int blocks)
        {
            inner.BurstEnded(reason, wn, il, k, snrTenthDb, blocks);
            owner._log?.Invoke(
                $"radio: rx {Waveform(wn, il)} {_bytes} bytes"
                + (snrTenthDb is int snr ? $", SNR {snr / 10.0:F1} dB" : "")
                + (reason == BurstEndReason.Eom ? "" : ", cut short"));
            _bytes = 0;
        }

        public void TransmitDone()
        {
            // Logged once the audio has gone out, so a burst that never left is not reported sent.
            if (Interlocked.Exchange(ref owner._sending, null) is { } line)
            {
                owner._log?.Invoke(line);
            }

            inner.TransmitDone();
        }

        public void ChannelBusy(bool busy) => inner.ChannelBusy(busy);
    }
}
