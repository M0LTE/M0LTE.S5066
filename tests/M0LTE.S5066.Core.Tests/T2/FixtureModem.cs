using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>The waveform asked for has no fixture entry: the T2 reason <c>unplanned_waveform</c>.</summary>
internal sealed class UnplannedWaveformException(int wn, Interleaver interleaver)
    : Exception($"no modem fixture entry for wn {wn} interleaver {InterleaverNames.ToName(interleaver)}");

/// <summary>
/// The fake modem of harness-contract.md section 2 item 2: <see cref="IModemPort.PlanBurst"/>
/// answers from the <c>modem</c> fixture by the arithmetic of corpus-format.md section 5.
/// </summary>
internal sealed class FixtureModem(ModemFixture fixture) : IModemPort
{
    public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ModemWaveform? entry = null;
        foreach (ModemWaveform w in fixture.Waveforms)
        {
            if (w.Wn == wn && w.Interleaver == interleaver)
            {
                entry = w;
                break;
            }
        }

        if (entry is null)
        {
            throw new UnplannedWaveformException(wn, interleaver);
        }

        int preamble = fixture.PreambleMs ?? throw new MissingSettingException("modem.preamble_ms");
        int flush = fixture.FlushMs ?? throw new MissingSettingException("modem.flush_ms");
        int blocks = byteCount == 0 ? 1 : (byteCount + entry.BlockBytes - 1) / entry.BlockBytes;
        return new BurstPlan(preamble, entry.BlockMs, flush, entry.BlockBytes, blocks,
            preamble + ((long)blocks * entry.BlockMs) + flush);
    }
}
