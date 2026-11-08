using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host.Modem;

/// <summary>
/// Plans a burst from a <see cref="ModemFixture"/> (the <c>modem.*</c> keys of corpus-format.md
/// section 6) by the arithmetic of corpus-format.md section 5, the same answer the T2 harness's
/// fake modem gives. Used by the byte-level <see cref="LoopbackMedium"/>, whose bursts have no
/// real modem behind them.
/// </summary>
public static class FixturePlanner
{
    /// <summary>Plans a burst of <paramref name="byteCount"/> bytes.</summary>
    /// <exception cref="ArgumentException">The fixture has no entry for the waveform and interleaver, or no preamble or flush.</exception>
    public static BurstPlan Plan(ModemFixture fixture, int wn, Interleaver interleaver, int byteCount)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ModemWaveform entry = fixture.Waveforms.FirstOrDefault(w => w.Wn == wn && w.Interleaver == interleaver)
            ?? throw new ArgumentException($"no modem.waveforms entry for wn {wn} interleaver {InterleaverNames.ToName(interleaver)}");
        int preamble = fixture.PreambleMs ?? throw new ArgumentException("modem.preamble_ms is not set");
        int flush = fixture.FlushMs ?? throw new ArgumentException("modem.flush_ms is not set");
        long used = (long)byteCount + fixture.EomBytes;
        int blocks = used == 0 ? 1 : (int)((used + entry.BlockBytes - 1) / entry.BlockBytes);
        return new BurstPlan(preamble, entry.BlockMs, flush, entry.BlockBytes, blocks,
            preamble + ((long)blocks * entry.BlockMs) + flush, fixture.EomBytes);
    }
}
