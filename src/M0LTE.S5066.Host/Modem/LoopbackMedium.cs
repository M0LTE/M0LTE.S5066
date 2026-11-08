using System.Diagnostics;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host.Modem;

/// <summary>
/// A byte-level shared channel for hosts in one process (core-architecture.md 12.8). Each
/// <see cref="IHostModem.Transmit"/> on one of its modems reaches every other modem as one burst
/// of blocks, timed in real time by the sender's <see cref="IModemPort.PlanBurst"/>: carrier
/// detect after the preamble, one block at the end of each block time, and the end of the burst
/// after the flush. No audio is involved. A test aid, not a model of HF.
/// </summary>
/// <remarks>
/// A modem that is transmitting or already receiving when a burst starts does not hear it (half
/// duplex, and one burst at a time). A modem that starts transmitting while it receives loses the
/// rest of that burst, which ends for it as signal_lost (core-architecture.md 12.5 rule 3).
/// </remarks>
public sealed class LoopbackMedium
{
    private readonly object _gate = new();
    private readonly List<LoopbackModem> _modems = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// Bursts dropped on the way to a receiver: called with (sender, receiver) per burst; true drops
    /// it for that receiver. Null drops nothing.
    /// </summary>
    public Func<LoopbackModem, LoopbackModem, bool>? Drop { get; set; }

    /// <summary>Adds a modem whose bursts are planned from <paramref name="fixture"/>.</summary>
    public LoopbackModem CreateModem(ModemFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var m = new LoopbackModem(this, fixture);
        lock (_gate)
        {
            _modems.Add(m);
        }

        return m;
    }

    private long NowMs => _clock.ElapsedMilliseconds;

    private void StartBurst(LoopbackModem sender, byte[] bytes, BurstPlan plan, int wn, Interleaver il, int k)
    {
        var burst = new Burst(sender, bytes, plan, wn, il, k);
        _ = Task.Run(() => RunAsync(burst));
    }

    private async Task RunAsync(Burst b)
    {
        long start = NowMs;
        await WaitUntil(start + b.Plan.PreambleMs).ConfigureAwait(false);
        List<LoopbackModem> hearers = [];
        lock (_gate)
        {
            foreach (LoopbackModem m in _modems)
            {
                if (m != b.Sender && m.Events is not null && !m.Transmitting && m.Receiving is null && Drop?.Invoke(b.Sender, m) != true)
                {
                    m.Receiving = b;
                    hearers.Add(m);
                    m.Events.BurstStarted();
                }
            }
        }

        for (int i = 0; i < b.Plan.Blocks; i++)
        {
            await WaitUntil(start + b.Plan.PreambleMs + ((long)(i + 1) * b.Plan.BlockMs)).ConfigureAwait(false);
            int from = i * b.Plan.BytesPerBlock;
            int len = Math.Clamp(b.Bytes.Length - from, 0, b.Plan.BytesPerBlock);
            byte[] block = new byte[b.Plan.BytesPerBlock];
            b.Bytes.AsSpan(from, len).CopyTo(block);
            lock (_gate)
            {
                foreach (LoopbackModem m in hearers)
                {
                    if (m.Receiving == b)
                    {
                        m.ReceivedBlocks++;
                        m.Events!.BytesReceived(block);
                    }
                }
            }
        }

        await WaitUntil(start + b.Plan.DurationMs).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (LoopbackModem m in hearers)
            {
                if (m.Receiving == b)
                {
                    m.Events!.BurstEnded(BurstEndReason.Eom, b.Wn, b.Interleaver, b.K, null, m.ReceivedBlocks);
                    m.Receiving = null;
                    m.ReceivedBlocks = 0;
                }
            }

            b.Sender.Transmitting = false;
            b.Sender.Events?.TransmitDone();
        }
    }

    private async Task WaitUntil(long ms)
    {
        long left = ms - NowMs;
        if (left > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(left)).ConfigureAwait(false);
        }
    }

    /// <summary>One modem on a <see cref="LoopbackMedium"/>.</summary>
    public sealed class LoopbackModem : IHostModem
    {
        private readonly LoopbackMedium _medium;
        private readonly ModemFixture _fixture;

        internal LoopbackModem(LoopbackMedium medium, ModemFixture fixture)
        {
            _medium = medium;
            _fixture = fixture;
        }

        internal IModemEvents? Events { get; private set; }

        internal bool Transmitting { get; set; }

        internal Burst? Receiving { get; set; }

        internal int ReceivedBlocks { get; set; }

        /// <inheritdoc/>
        public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) =>
            FixturePlanner.Plan(_fixture, wn, interleaver, byteCount);

        /// <inheritdoc/>
        public void Start(IModemEvents events)
        {
            ArgumentNullException.ThrowIfNull(events);
            lock (_medium._gate)
            {
                Events = events;
            }
        }

        /// <inheritdoc/>
        public void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom)
        {
            BurstPlan plan = PlanBurst(wn, interleaver, bytes.Length);
            lock (_medium._gate)
            {
                if (Transmitting)
                {
                    throw new InvalidOperationException("a burst is already being sent (core-architecture.md 12.5 rule 4)");
                }

                Transmitting = true;
                if (Receiving is { } r)
                {
                    Events!.BurstEnded(BurstEndReason.SignalLost, r.Wn, r.Interleaver, r.K, null, ReceivedBlocks);
                    Receiving = null;
                    ReceivedBlocks = 0;
                }
            }

            _medium.StartBurst(this, bytes.ToArray(), plan, wn, interleaver, k);
        }

        /// <inheritdoc/>
        public void TerminateReceive()
        {
        }

        /// <inheritdoc/>
        public void SetMaxInputBlocks(int n)
        {
        }
    }

    internal sealed record Burst(LoopbackModem Sender, byte[] Bytes, BurstPlan Plan, int Wn, Interleaver Interleaver, int K);
}
