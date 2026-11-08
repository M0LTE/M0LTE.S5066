using M0LTE.Ms110d;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host.Audio;

namespace M0LTE.S5066.Host.Modem;

/// <summary>
/// The modem port over M0LTE.Ms110d (core-architecture.md 12.6): MIL-STD-188-110D Appendix D
/// waveforms on an <see cref="IAudioChannel"/> at 9600 Hz.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Bytes go to the modulator least significant bit first and decoded blocks are packed back the same way (C-0150).</item>
/// <item>Waveform numbers map one to one; interleavers ultra short, short, medium and long. Very short and very long are not 110D interleavers and are refused (plan 5.3).</item>
/// <item>Receive is gated while transmitting, and the demodulator is reset after each transmission (12.5 rule 3).</item>
/// <item>Modulation runs on a worker thread, never on the caller's (the host's loop), so a long burst never holds up the core (issue #80).</item>
/// <item>The adapter raises no <see cref="IModemEvents.ChannelBusy"/>.</item>
/// </list>
/// </remarks>
public sealed class Ms110dHostModem : IHostModem
{
    private const double SamplesPerMs = IAudioChannel.SampleRate / 1000.0;
    private const int SamplesPerSymbol = IAudioChannel.SampleRate / 2400;

    private readonly IAudioChannel _audio;
    private readonly Ms110dDemodulator _demod;
    private readonly object _gate = new();
    private readonly float _amplitude;
    private readonly Func<Ms110dTxSettings, byte[], float[]> _modulate;
    private readonly Action<string>? _log;
    private IModemEvents? _events;
    private bool _transmitting;
    private bool _resetPending;
    private Ms110dLockInfo? _locked;
    private int _blocks;

    /// <summary>Creates the adapter.</summary>
    /// <param name="audio">The audio path.</param>
    /// <param name="amplitude">Transmit amplitude, a fraction of full scale (the modulator's default is 0.5).</param>
    /// <param name="log">A sink for one-line log messages, or null.</param>
    public Ms110dHostModem(IAudioChannel audio, float amplitude = 0.5f, Action<string>? log = null)
        : this(audio, amplitude, log, static (settings, bits) => new Ms110dModulator(settings).Modulate(bits))
    {
    }

    /// <summary>Creates the adapter with a stand-in modulator, for tests.</summary>
    internal Ms110dHostModem(IAudioChannel audio, float amplitude, Action<string>? log, Func<Ms110dTxSettings, byte[], float[]> modulate)
    {
        ArgumentNullException.ThrowIfNull(audio);
        _audio = audio;
        _amplitude = amplitude;
        _log = log;
        _modulate = modulate;
        _demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        _demod.BurstStarted += OnBurstStarted;
        _demod.BlockDecoded += OnBlockDecoded;
        _demod.BurstCompleted += OnBurstCompleted;
    }

    /// <summary>A sink for the per-burst trace (issue #86): modulation and playout timing. Null for none.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>
    /// The plan for a burst, converted from <see cref="Ms110dBurstPlan.Plan"/> so it is never early
    /// (core-architecture.md 12.5 rule 1; C-0891): preamble, one block and the flush (the leading
    /// mini-probe, the EOT marker and the pulse-shaping tail) are each rounded up to a whole ms. The
    /// 32-bit EOM counts against the blocks, since every burst the core sends asks for it.
    /// </summary>
    public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        Ms110dBurstPlan p = Ms110dBurstPlan.Plan(Settings(wn, interleaver, 7, true), byteCount * 8);
        int blockSymbols = (int)Math.Round(p.BlockSeconds * 2400);
        int flushSamples = p.Samples - ((p.PreambleSymbols + (p.Blocks * blockSymbols)) * SamplesPerSymbol);
        int preambleMs = CeilMs(p.PreambleSymbols * SamplesPerSymbol);
        int blockMs = CeilMs(blockSymbols * SamplesPerSymbol);
        int flushMs = CeilMs(flushSamples);
        return new BurstPlan(preambleMs, blockMs, flushMs, p.BytesPerBlock, p.Blocks, preambleMs + ((long)p.Blocks * blockMs) + flushMs);
    }

    /// <inheritdoc/>
    public void Start(IModemEvents events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        _audio.Start(OnAudio);
    }

    /// <summary>
    /// Starts one burst and returns at once (core-architecture.md 12.5 rule 4). Receive is gated
    /// and a locked burst ended here, on the caller's thread; the audio is modulated on a worker
    /// thread and then played, so the host's loop never waits for the modulator (issue #80).
    /// </summary>
    public void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom)
    {
        // Settings and bits here, so a bad waveform or interleaver throws to the caller and the
        // caller may reuse its buffer once this returns.
        Ms110dTxSettings settings = Settings(wn, interleaver, k, eom) with { Amplitude = _amplitude };
        byte[] bits = ToBits(bytes.Span);
        lock (_gate)
        {
            if (_transmitting)
            {
                throw new InvalidOperationException("a burst is already being sent (core-architecture.md 12.5 rule 4)");
            }

            _transmitting = true;
            EndLockedBurst(BurstEndReason.SignalLost, null);
        }

        long asked = System.Diagnostics.Stopwatch.GetTimestamp();
        _ = Task.Run(() =>
        {
            try
            {
                float[] samples = _modulate(settings, bits);
                Trace?.Invoke($"modulated {samples.Length} samples ({samples.Length / SamplesPerMs:0} ms of audio) in {System.Diagnostics.Stopwatch.GetElapsedTime(asked).TotalMilliseconds:0} ms; queued for playout");
                _audio.Play(samples, OnPlayed);
            }
            catch (Exception e)
            {
                // Nothing went out. Report the burst done anyway, so the core is not left waiting.
                _log?.Invoke($"modem: burst not sent: {e.Message}");
                OnPlayed();
            }
        });
    }

    /// <inheritdoc/>
    public void TerminateReceive() => _demod.Terminate();

    /// <inheritdoc/>
    public void SetMaxInputBlocks(int n) => _demod.MaxInputDataBlocks = n;

    /// <summary>Converts bytes to the modulator's 0/1 bits, least significant bit first (C-0150).</summary>
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

    /// <summary>Packs 0/1 bits back into bytes, least significant bit first; a partial last byte is dropped.</summary>
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

    private void OnPlayed()
    {
        lock (_gate)
        {
            _transmitting = false;
            _resetPending = true;
        }

        _events?.TransmitDone();
    }

    private static int CeilMs(int samples) => (int)Math.Ceiling(samples / SamplesPerMs);

    private static Ms110dTxSettings Settings(int wn, Interleaver interleaver, int k, bool eom) => new()
    {
        WaveformNumber = wn,
        Interleaver = interleaver switch
        {
            Interleaver.UltraShort => Ms110dInterleaverKind.UltraShort,
            Interleaver.Short => Ms110dInterleaverKind.Short,
            Interleaver.Medium => Ms110dInterleaverKind.Medium,
            Interleaver.Long => Ms110dInterleaverKind.Long,
            _ => throw new ArgumentException($"{InterleaverNames.ToName(interleaver)} is not a 110D interleaver (plan 5.3)", nameof(interleaver)),
        },
        ConstraintLength = k,
        AppendEom = eom,
        AppendEot = true,
    };

    private static Interleaver FromKind(Ms110dInterleaverKind kind) => kind switch
    {
        Ms110dInterleaverKind.UltraShort => Interleaver.UltraShort,
        Ms110dInterleaverKind.Short => Interleaver.Short,
        Ms110dInterleaverKind.Medium => Interleaver.Medium,
        _ => Interleaver.Long,
    };

    private void OnAudio(ReadOnlyMemory<float> samples)
    {
        lock (_gate)
        {
            if (_transmitting)
            {
                return;
            }

            if (_resetPending)
            {
                _resetPending = false;
                _locked = null;
                _demod.Reset();
            }

            _demod.Process(samples.Span);
        }
    }

    // The demodulator's events are raised from Process, so they run under _gate.
    private void OnBurstStarted(Ms110dBurstStart start)
    {
        if (_transmitting)
        {
            return;
        }

        _locked = start.Lock;
        _blocks = 0;
        _events?.BurstStarted();
    }

    private void OnBlockDecoded(Ms110dRxBlock block)
    {
        if (_locked is null)
        {
            return;
        }

        _blocks++;
        _events?.BytesReceived(ToBytes(block.Bits));
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
        _events?.BurstEnded(reason, l.WaveformNumber, FromKind(l.Interleaver), l.ConstraintLength, snrTenthDb, _blocks);
    }
}
