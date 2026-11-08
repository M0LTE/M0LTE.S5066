namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The answer to a burst plan (plan 5.1; corpus-format.md 5): the duration of a burst carrying a
/// given number of bytes, and the block structure the planner lays D_PDUs out against
/// (core-architecture.md 5.3, 5.6). Block i starts on air at <c>PreambleMs + i * BlockMs</c>.
/// The modem's end-of-message marker, when it takes bit space in the blocks after the last byte,
/// is <c>EomBytes</c>: n blocks carry at most <c>n * BytesPerBlock - EomBytes</c> bytes (interp C-1290).
/// </summary>
/// <param name="PreambleMs">Preamble duration in ms.</param>
/// <param name="BlockMs">Duration of one block in ms.</param>
/// <param name="FlushMs">Flush duration after the last block in ms.</param>
/// <param name="BytesPerBlock">Bytes carried by one block.</param>
/// <param name="Blocks">Blocks needed for the byte count and the end-of-message marker, at least 1.</param>
/// <param name="DurationMs">PreambleMs + Blocks * BlockMs + FlushMs.</param>
/// <param name="EomBytes">Bytes of block space the end-of-message marker takes after the last byte, 0 or more.</param>
public sealed record BurstPlan(int PreambleMs, int BlockMs, int FlushMs, int BytesPerBlock, int Blocks, long DurationMs, int EomBytes)
{
    /// <summary>The most bytes <paramref name="blocks"/> blocks carry, after the end-of-message marker (interp C-1290).</summary>
    /// <param name="blocks">Block count, 1 or more.</param>
    public long CapacityOf(long blocks) => Math.Max(0, (blocks * BytesPerBlock) - EomBytes);

    /// <summary>The blocks a burst of <paramref name="bytes"/> bytes needs, the marker included, at least 1 (interp C-1290).</summary>
    /// <param name="bytes">Bytes to send, 0 or more.</param>
    public long BlocksFor(long bytes) => Math.Max(1, (bytes + EomBytes + BytesPerBlock - 1) / BytesPerBlock);
}

/// <summary>
/// The core's only call out (core-architecture.md 1). It is pure: the same arguments always give
/// the same answer. A transmission itself is the <see cref="Transmit"/> output, not a call here.
/// </summary>
public interface IModemPort
{
    /// <summary>Plans a burst of <paramref name="byteCount"/> bytes on a waveform and interleaver.</summary>
    /// <param name="wn">Waveform number.</param>
    /// <param name="interleaver">Interleaver.</param>
    /// <param name="byteCount">Bytes to send, 0 or more.</param>
    BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount);
}
