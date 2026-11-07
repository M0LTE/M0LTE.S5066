namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The configuration of one node (core-architecture.md 7.1). The keys follow the T2
/// <c>defaults.json</c> of corpus-format.md section 6. Settings for later items (<c>groups</c>,
/// <c>time.*</c>, <c>dts.*</c>, <c>cas.*</c>, <c>sis.*</c>, <c>drc.*</c>) are added by the item
/// that first needs them (core-architecture.md 7.2), with their keys in <c>defaults.json</c>.
/// <c>prng_seed</c> is not a field: it is a constructor argument of <see cref="Node"/>, so a
/// run-time change can never re-seed the generator.
/// </summary>
/// <param name="LocalAddress">local_address: this node's 28-bit address.</param>
/// <param name="AddressSize">address_size: the size-of-address field the node sends, 1 to 7 (C-0133).</param>
/// <param name="Csma">csma.*.</param>
/// <param name="Modem">modem.*: the T2 modem fixture; the host ignores it.</param>
public sealed record NodeConfig(uint LocalAddress, int AddressSize, CsmaConfig Csma, ModemFixture Modem)
{
    /// <summary>
    /// groups: the group addresses this node receives, as 28-bit values. Membership is
    /// configuration, since no protocol joins a group (A-0119, A-0120). Default empty.
    /// </summary>
    public IReadOnlyList<uint> Groups { get; init; } = [];

    /// <summary>time.*: the UTC instant of core time 0, for the TTD of DATA S_PDUs (A-0251).</summary>
    public TimeConfig Time { get; init; } = new(2026, 1, 0);

    /// <summary>sis.*: SIS settings.</summary>
    public SisConfig Sis { get; init; } = new(SisConfig.MaxTtl);
}

/// <summary>
/// time.* settings: the UTC year, day-of-year and seconds-of-day that correspond to core time 0.
/// The core has no clock, so the host supplies these and the SIS adds the core time to them
/// with integer calendar arithmetic (core-architecture.md 3.1; A-0161, A-0166, A-0251).
/// </summary>
/// <param name="EpochYear">time.epoch_year, the Gregorian year.</param>
/// <param name="EpochDay">time.epoch_day, 1 to 365, or 366 in a leap year (A-0161).</param>
/// <param name="EpochSecond">time.epoch_second, 0 to 86399 (A-0166).</param>
public sealed record TimeConfig(int EpochYear, int EpochDay, int EpochSecond);

/// <summary>sis.* settings.</summary>
/// <param name="DefaultTtl">
/// sis.default_ttl: the default maximum TTL in the 2-second units of the TTL field, used when a
/// request gives TTL 0 (A-0087, A-0088, A-0089, A-0091). 1 to <see cref="MaxTtl"/>.
/// </param>
public sealed record SisConfig(int DefaultTtl)
{
    /// <summary>The largest TTL the field may carry, 648000 units of 2 s, about 15 days (S-0116, S-0121).</summary>
    public const int MaxTtl = 648000;
}

/// <summary>The Annex K contention mode (decision D7; K-0107 ruling adds ed3).</summary>
public enum CsmaMode
{
    /// <summary>jitter.</summary>
    Jitter,

    /// <summary>slotted.</summary>
    Slotted,

    /// <summary>ed3: jitter, one common LBT value equal to lbt_wait_dcd_ms, LBT rule 1 off (K-0107).</summary>
    Ed3,
}

/// <summary>csma.* settings [Ed4 K Table 7, p.K-12].</summary>
/// <param name="Mode">csma.mode, or null when not configured (no default).</param>
/// <param name="ContSlotWidthMs">csma.cont_slot_width_ms.</param>
/// <param name="NumContSlots">csma.num_cont_slots.</param>
/// <param name="LbtWaitDcdMs">csma.lbt_wait_dcd_ms.</param>
/// <param name="LbtWaitVdcdMs">csma.lbt_wait_vdcd_ms.</param>
/// <param name="NodeSlotPosition">csma.node_slot_position, or null (no default, K-0104).</param>
public sealed record CsmaConfig(CsmaMode? Mode, int ContSlotWidthMs, int NumContSlots, int LbtWaitDcdMs,
                                int LbtWaitVdcdMs, int? NodeSlotPosition)
{
    /// <summary>The mode, for code that contends. Throws <see cref="MissingSettingException"/> when unset.</summary>
    public CsmaMode RequireMode() => Mode ?? throw new MissingSettingException("csma.mode");

    /// <summary>The slot position, for slotted mode. Throws <see cref="MissingSettingException"/> when unset.</summary>
    public int RequireNodeSlotPosition() => NodeSlotPosition ?? throw new MissingSettingException("csma.node_slot_position");
}

/// <summary>One entry of the modem fixture's waveform table (corpus-format.md 5).</summary>
/// <param name="Wn">Waveform number.</param>
/// <param name="Interleaver">Interleaver.</param>
/// <param name="BlockBytes">Bytes per block, positive.</param>
/// <param name="BlockMs">Duration of one block in ms, positive.</param>
public sealed record ModemWaveform(int Wn, Interleaver Interleaver, int BlockBytes, int BlockMs);

/// <summary>modem.*: the T2 modem fixture (corpus-format.md 5). Test data, not claims about 110D.</summary>
/// <param name="PreambleMs">modem.preamble_ms, or null (no default).</param>
/// <param name="FlushMs">modem.flush_ms, or null (no default).</param>
/// <param name="Waveforms">modem.waveforms.</param>
public sealed record ModemFixture(int? PreambleMs, int? FlushMs, IReadOnlyList<ModemWaveform> Waveforms);

/// <summary>A configuration value is unusable: wrong type or out of range. A bug in the caller, never a protocol event.</summary>
public class ConfigException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="setting">The dotted key.</param>
    /// <param name="message">What is wrong.</param>
    public ConfigException(string setting, string message)
        : base($"{setting}: {message}") => Setting = setting;

    /// <summary>The dotted key, for example <c>csma.mode</c>.</summary>
    public string Setting { get; }
}

/// <summary>
/// The core needs a setting that has no value (null in <c>defaults.json</c> and not supplied).
/// A T2 harness reports it as <c>missing_setting</c> (harness-contract.md 2).
/// </summary>
public sealed class MissingSettingException : ConfigException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="setting">The dotted key.</param>
    public MissingSettingException(string setting)
        : base(setting, "the setting is null and the core needs it")
    {
    }
}
