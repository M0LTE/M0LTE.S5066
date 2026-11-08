namespace M0LTE.S5066.Core.Runtime;

/// <summary>The Ed4 interleaver names of the T2 enum (C-0469, plan 5.3).</summary>
public enum Interleaver
{
    /// <summary>ultra_short.</summary>
    UltraShort,

    /// <summary>very_short.</summary>
    VeryShort,

    /// <summary>short.</summary>
    Short,

    /// <summary>medium.</summary>
    Medium,

    /// <summary>long.</summary>
    Long,

    /// <summary>very_long.</summary>
    VeryLong,
}

/// <summary>Why a received burst ended: the T2 <c>reason</c> of <c>burst_ended</c>.</summary>
public enum BurstEndReason
{
    /// <summary>eom: the modem saw the end-of-message marker.</summary>
    Eom,

    /// <summary>signal_lost.</summary>
    SignalLost,

    /// <summary>terminated: the reception was cut short on request.</summary>
    Terminated,
}

/// <summary>
/// One input to the core, at time <paramref name="T"/>. The ten kinds match the T2 inputs of the
/// conformance repository's corpus-format.md section 4.2 key for key (core-architecture.md 2.1).
/// </summary>
/// <param name="T">Milliseconds since the core was created; never decreases from one input to the next.</param>
public abstract record CoreInput(long T);

/// <summary>Class 0: the whole new configuration (the harness or host has done any overlay merge).</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="NewConfig">The configuration that replaces the one in force.</param>
public sealed record ConfigInput(long T, NodeConfig NewConfig) : CoreInput(T);

/// <summary>Class 1: a timer the core started has reached its deadline.</summary>
/// <param name="T">Input time in ms; equals the timer's deadline.</param>
/// <param name="Name">The timer.</param>
public sealed record TimerExpired(long T, TimerName Name) : CoreInput(T);

/// <summary>Class 2: the modem detected the start of a burst (carrier detect).</summary>
/// <param name="T">Input time in ms.</param>
public sealed record BurstStarted(long T) : CoreInput(T);

/// <summary>Class 2: decoded bytes from one modem block.</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="Bytes">The bytes.</param>
public sealed record BytesReceived(long T, ReadOnlyMemory<byte> Bytes) : CoreInput(T);

/// <summary>Class 2: end of a received burst and its quality.</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="Reason">Why it ended.</param>
/// <param name="Wn">Waveform number.</param>
/// <param name="Interleaver">Interleaver.</param>
/// <param name="K">Constraint length.</param>
/// <param name="SnrTenthDb">SNR in tenths of a dB, or null when the modem gives none.</param>
/// <param name="Blocks">Number of blocks decoded.</param>
public sealed record BurstEnded(long T, BurstEndReason Reason, int Wn, Interleaver Interleaver, int K,
                                int? SnrTenthDb, int Blocks) : CoreInput(T);

/// <summary>Class 2: the core's own transmission has finished on air.</summary>
/// <param name="T">Input time in ms.</param>
public sealed record TransmitDone(long T) : CoreInput(T);

/// <summary>Class 2: the optional external busy input (plan 7.3).</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="Busy">True when the external detector says busy.</param>
public sealed record ChannelBusy(long T, bool Busy) : CoreInput(T);

/// <summary>Class 3: a SIS client connected.</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="Conn">Connection number, positive, never reused.</param>
public sealed record SisConnect(long T, int Conn) : CoreInput(T);

/// <summary>Class 3: bytes from a SIS client's stream, possibly part of a frame or several frames.</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="Conn">Connection number.</param>
/// <param name="Bytes">The bytes.</param>
public sealed record SisBytes(long T, int Conn, ReadOnlyMemory<byte> Bytes) : CoreInput(T);

/// <summary>Class 3: the SIS client went away.</summary>
/// <param name="T">Input time in ms.</param>
/// <param name="Conn">Connection number.</param>
public sealed record SisDisconnect(long T, int Conn) : CoreInput(T);

/// <summary>
/// The host is shutting the node down (interp A-0059). Not a T2 input kind: it reaches the core
/// only through <see cref="Node.Shutdown(long)"/>, the last call before the host destroys the core
/// (core-architecture.md 10 item 1, STOP).
/// </summary>
internal sealed record ShutdownInput(long T) : CoreInput(T);
