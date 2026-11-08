using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host.Modem;

/// <summary>
/// The modem port a host drives (core-architecture.md 12.5; plan 5.1). It extends the core's pure
/// <see cref="IModemPort.PlanBurst"/> with what a real modem does: transmit a burst, and report
/// what it hears through <see cref="IModemEvents"/>.
/// </summary>
/// <remarks>
/// Rules every implementation follows (core-architecture.md 12.5):
/// <list type="number">
/// <item><see cref="IModemPort.PlanBurst"/> is pure and never early: each duration is rounded up to a whole ms, so an EOT computed from it is never before the true end (C-0891).</item>
/// <item><see cref="IModemEvents.BurstStarted"/> once per burst the modem has locked to, then <see cref="IModemEvents.BytesReceived"/> once per decoded block, then exactly one <see cref="IModemEvents.BurstEnded"/>.</item>
/// <item>Half duplex: no receive events while transmitting.</item>
/// <item><see cref="Transmit"/> returns at once; <see cref="IModemEvents.TransmitDone"/> follows once per call, when the last sample has left the output.</item>
/// <item>Bytes are bytes; the bit order on the air is the port's business (C-0150).</item>
/// <item><see cref="IModemEvents.ChannelBusy"/> is the external busy input (plan 7.3) and nothing else is DCD.</item>
/// </list>
/// </remarks>
public interface IHostModem : IModemPort
{
    /// <summary>Starts the modem. Events may be raised on any thread from now on.</summary>
    /// <param name="events">Where the modem reports what it hears and when its bursts end.</param>
    void Start(IModemEvents events);

    /// <summary>Starts one burst and returns at once (plan 5.1; 110D D.5.4.6 a, b).</summary>
    /// <param name="bytes">The bytes of one whole transmission.</param>
    /// <param name="wn">Waveform number.</param>
    /// <param name="interleaver">Interleaver.</param>
    /// <param name="k">Constraint length.</param>
    /// <param name="eom">True to end the burst with the modem's end-of-message marker.</param>
    void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom);

    /// <summary>Stops the reception in progress (110D D.5.4.6 d). Not used in phases 3 to 6.</summary>
    void TerminateReceive();

    /// <summary>Sets the receiver's maximum input data blocks, 0 for unlimited (110D D.5.4.6 c). Not used in phases 3 to 6.</summary>
    /// <param name="n">The maximum.</param>
    void SetMaxInputBlocks(int n);
}

/// <summary>
/// What a modem reports to its host (plan 5.1). Calls may come from any thread; the host stamps
/// each with its own clock when it arrives (core-architecture.md 12.2) and queues it as a class 2
/// input in the order received (corpus-format.md 4.5).
/// </summary>
public interface IModemEvents
{
    /// <summary>A burst has started and the modem has locked to it: carrier detect.</summary>
    void BurstStarted();

    /// <summary>The bytes of one decoded block.</summary>
    /// <param name="bytes">The bytes.</param>
    void BytesReceived(ReadOnlyMemory<byte> bytes);

    /// <summary>The burst has ended.</summary>
    /// <param name="reason">Why it ended.</param>
    /// <param name="wn">Waveform number of the burst.</param>
    /// <param name="interleaver">Interleaver of the burst.</param>
    /// <param name="k">Constraint length of the burst.</param>
    /// <param name="snrTenthDb">SNR in tenths of a dB, or null when the modem has none.</param>
    /// <param name="blocks">Blocks decoded.</param>
    void BurstEnded(BurstEndReason reason, int wn, Interleaver interleaver, int k, int? snrTenthDb, int blocks);

    /// <summary>The burst started by the last <see cref="IHostModem.Transmit"/> has left the output.</summary>
    void TransmitDone();

    /// <summary>The external busy input changed (plan 7.3; P5-4).</summary>
    /// <param name="busy">True when busy.</param>
    void ChannelBusy(bool busy);
}

/// <summary>
/// A modem port that wraps another, adding to it (a busy input, logging) without changing what it
/// sends or hears. The host looks through it to the modem inside, for example to give an
/// <see cref="Ms110dHostModem"/> its trace sink.
/// </summary>
public interface IHostModemWrapper
{
    /// <summary>The modem inside.</summary>
    IHostModem Inner { get; }
}
