namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// Link state names for the <c>link_state</c> output (core-architecture.md 2.2). Each is
/// registered in corpus-format.md section 4.6 by the CAS item that first emits it (P4-1, P5-1).
/// </summary>
public enum LinkStateName
{
    /// <summary>unlinked.</summary>
    Unlinked,

    /// <summary>calling.</summary>
    Calling,

    /// <summary>linked.</summary>
    Linked,

    /// <summary>closing.</summary>
    Closing,

    /// <summary>breaking.</summary>
    Breaking,
}

/// <summary>
/// One output of the core. The eight kinds match the T2 outputs of corpus-format.md section 4.3
/// key for key (core-architecture.md 2.2). Outputs carry no time: they happen at the time of the
/// input that caused them.
/// </summary>
public abstract record CoreOutput;

/// <summary>One whole transmission, sent as one modem burst.</summary>
/// <param name="Bytes">The bytes to send.</param>
/// <param name="Wn">Waveform number.</param>
/// <param name="Interleaver">Interleaver.</param>
/// <param name="K">Constraint length.</param>
/// <param name="Eom">True when the modem ends the burst with its end-of-message marker.</param>
public sealed record Transmit(ReadOnlyMemory<byte> Bytes, int Wn, Interleaver Interleaver, int K, bool Eom) : CoreOutput;

/// <summary>Ask the modem to stop the current reception. Not emitted in phases 3 to 6.</summary>
public sealed record TerminateReceive : CoreOutput;

/// <summary>Change the modem's maximum input data blocks. Not emitted in phases 3 to 6.</summary>
/// <param name="N">The new maximum.</param>
public sealed record SetMaxInputBlocks(int N) : CoreOutput;

/// <summary>Start, or restart, the named timer to expire <paramref name="Ms"/> ms from now.</summary>
/// <param name="Name">The timer.</param>
/// <param name="Ms">Duration, at least 1.</param>
public sealed record TimerStart(TimerName Name, long Ms) : CoreOutput;

/// <summary>Stop the named timer. The core emits it only for a running timer.</summary>
/// <param name="Name">The timer.</param>
public sealed record TimerCancel(TimerName Name) : CoreOutput;

/// <summary>Bytes to write to a SIS client's stream: one encoded primitive per output.</summary>
/// <param name="Conn">Connection number.</param>
/// <param name="Bytes">The bytes.</param>
public sealed record SisBytesOut(int Conn, ReadOnlyMemory<byte> Bytes) : CoreOutput;

/// <summary>Close a SIS client connection.</summary>
/// <param name="Conn">Connection number.</param>
public sealed record SisClose(int Conn) : CoreOutput;

/// <summary>A link to a peer changed state.</summary>
/// <param name="Peer">The peer's 28-bit address.</param>
/// <param name="State">The new state.</param>
public sealed record LinkState(uint Peer, LinkStateName State) : CoreOutput;
