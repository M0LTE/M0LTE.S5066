using M0LTE.Ms110d;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Sweep;

/// <summary>
/// The in-process, synchronous MS110D modem of one simulated node (P7-8). It keeps the rules of
/// core-architecture.md 12.5 and the choices of the host adapter (12.6), on a virtual clock in
/// 20 ms chunks instead of real audio: bursts are planned exactly as <see cref="Ms110dHostModem"/>
/// plans them (the planner is that adapter's), modulated at once and played from the next chunk;
/// receive is gated while transmitting and the demodulator is reset after each transmission; a
/// <c>Transmit</c> while a burst is locked ends that burst as signal_lost; a burst that never
/// locked raises nothing. Events are handed back with the chunk's end time, as a real host stamps
/// them when its loop receives them (12.2).
/// </summary>
internal sealed class SimModem : IModemPort
{
    public const int ChunkSamples = ChannelSpec.SampleRate / 50;
    public const int ChunkMs = 20;

    private readonly Ms110dHostModem _planner = new(new NoAudio());
    private readonly Ms110dDemodulator _demod = new(new Ms110dDemodOptions());
    private readonly bool _ssb;
    private readonly List<Func<long, CoreInput>> _events = [];
    private float[]? _tx;
    private int _txPos;
    private bool _resetPending;
    private Ms110dLockInfo? _locked;
    private int _blocks;

    public SimModem(bool ssb)
    {
        _ssb = ssb;
        _demod.BurstStarted += OnBurstStarted;
        _demod.BlockDecoded += OnBlockDecoded;
        _demod.BurstCompleted += OnBurstCompleted;
    }

    /// <summary>Observers for the statistics: burst locked, block decoded, burst ended (reason, SNR in tenths).</summary>
    public Action? RxStarted { get; set; }

    public Action<byte[]>? RxBlock { get; set; }

    public Action<BurstEndReason, int, Interleaver, int?>? RxEnded { get; set; }

    public bool Transmitting => _tx is not null;

    /// <summary>The samples of the burst still to play.</summary>
    public int TxRemaining => _tx is null ? 0 : _tx.Length - _txPos;

    public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) => _planner.PlanBurst(wn, interleaver, byteCount);

    /// <summary>Starts a burst: modulated now, played from the next chunk. Returns its length in samples.</summary>
    public int Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver il, int k, bool eom)
    {
        if (_tx is not null)
        {
            throw new InvalidOperationException("a burst is already being sent (core-architecture.md 12.5 rule 4)");
        }

        Ms110dTxSettings settings = Rung.Settings(wn, il, k, eom) with { Amplitude = RungPower.Amplitude };
        float[] samples = new Ms110dModulator(settings).Modulate(ToBits(bytes.Span));
        float gain = RungPower.Gain(_ssb, new Rung(wn, il));
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }

        _tx = samples;
        _txPos = 0;
        EndLockedBurst(BurstEndReason.SignalLost, null);
        return samples.Length;
    }

    public void TerminateReceive() => _demod.Terminate();

    public void SetMaxInputBlocks(int n) => _demod.MaxInputDataBlocks = n;

    /// <summary>
    /// Fills one chunk of transmitted audio (silence when idle). Returns the samples of the burst
    /// in this chunk when its last sample went out in it (TransmitDone is then due), else -1.
    /// </summary>
    public int FillTx(Span<float> chunk)
    {
        chunk.Clear();
        if (_tx is null)
        {
            return -1;
        }

        int n = Math.Min(chunk.Length, _tx.Length - _txPos);
        _tx.AsSpan(_txPos, n).CopyTo(chunk);
        _txPos += n;
        return _txPos == _tx.Length ? n : -1;
    }

    /// <summary>Marks the burst played: receive opens again from the next chunk, with a fresh demodulator.</summary>
    public void TxFinished()
    {
        _tx = null;
        _resetPending = true;
    }

    /// <summary>Demodulates one chunk of received audio, unless transmitting. Events go to the returned list, stamped later by the caller.</summary>
    public void Receive(ReadOnlySpan<float> chunk)
    {
        if (_tx is not null)
        {
            return;
        }

        if (_resetPending)
        {
            _resetPending = false;
            _locked = null;
            _demod.Reset();
        }

        _demod.Process(chunk);
    }

    /// <summary>The modem events raised since the last call, in order, as input builders from a stamp.</summary>
    public List<Func<long, CoreInput>> TakeEvents()
    {
        var e = new List<Func<long, CoreInput>>(_events);
        _events.Clear();
        return e;
    }

    private void OnBurstStarted(Ms110dBurstStart start)
    {
        if (_tx is not null)
        {
            return;
        }

        _locked = start.Lock;
        _blocks = 0;
        _events.Add(t => new Core.Runtime.BurstStarted(t));
        RxStarted?.Invoke();
    }

    private void OnBlockDecoded(Ms110dRxBlock block)
    {
        if (_locked is null)
        {
            return;
        }

        _blocks++;
        byte[] bytes = ToBytes(block.Bits);
        _events.Add(t => new BytesReceived(t, bytes));
        RxBlock?.Invoke(bytes);
    }

    private void OnBurstCompleted(Ms110dBurst burst)
    {
        BurstEndReason reason = burst.Reason switch
        {
            Ms110dBurstEndReason.Eom or Ms110dBurstEndReason.MaxInputDataBlocks => BurstEndReason.Eom,
            Ms110dBurstEndReason.Terminated => BurstEndReason.Terminated,
            _ => BurstEndReason.SignalLost,
        };
        EndLockedBurst(reason, burst.SnrDb is double snr ? (int)Math.Round(snr * 10, MidpointRounding.AwayFromZero) : null);
    }

    private void EndLockedBurst(BurstEndReason reason, int? snrTenthDb)
    {
        if (_locked is not { } l)
        {
            return;
        }

        _locked = null;
        Interleaver il = l.Interleaver switch
        {
            Ms110dInterleaverKind.UltraShort => Interleaver.UltraShort,
            Ms110dInterleaverKind.Short => Interleaver.Short,
            Ms110dInterleaverKind.Medium => Interleaver.Medium,
            _ => Interleaver.Long,
        };
        int wn = l.WaveformNumber, k = l.ConstraintLength, blocks = _blocks;
        _events.Add(t => new BurstEnded(t, reason, wn, il, k, snrTenthDb, blocks));
        RxEnded?.Invoke(reason, wn, il, snrTenthDb);
    }

    /// <summary>Bytes to the modulator's 0/1 bits, least significant bit first (C-0150), as the host adapter does.</summary>
    internal static byte[] ToBits(ReadOnlySpan<byte> bytes)
    {
        byte[] bits = new byte[bytes.Length * 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int b = 0; b < 8; b++)
            {
                bits[(i * 8) + b] = (byte)((bytes[i] >> b) & 1);
            }
        }

        return bits;
    }

    /// <summary>0/1 bits back to bytes, least significant bit first; a partial last byte is dropped.</summary>
    internal static byte[] ToBytes(ReadOnlySpan<byte> bits)
    {
        byte[] bytes = new byte[bits.Length / 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            int v = 0;
            for (int b = 0; b < 8; b++)
            {
                v |= (bits[(i * 8) + b] & 1) << b;
            }

            bytes[i] = (byte)v;
        }

        return bytes;
    }

    /// <summary>The planner's audio channel: never started, never played.</summary>
    private sealed class NoAudio : IAudioChannel
    {
        public void Start(Action<ReadOnlyMemory<float>> received) => throw new NotSupportedException();

        public void Play(float[] samples, Action played) => throw new NotSupportedException();
    }
}
