namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The configuration of one node (core-architecture.md 7.1). The keys follow the T2
/// <c>defaults.json</c> of corpus-format.md section 6. Settings for later items (<c>groups</c>,
/// <c>time.*</c>, <c>dts.*</c>, <c>cas.*</c>, <c>sis.*</c>, <c>drc.*</c>) are added by the item
/// that first needs them (core-architecture.md 7.2), with their keys in <c>defaults.json</c>.
/// <c>prng_seed</c> is not a field: it is a constructor argument of <see cref="Node"/>, so a
/// run-time change can never re-seed the generator.
/// </summary>
/// <param name="LocalAddress">
/// local_address: this node's 28-bit address. There is no address size setting: each D_PDU is sent
/// with the smallest address size that holds its source and destination (C-0133).
/// </param>
/// <param name="Csma">csma.*.</param>
/// <param name="Modem">modem.*: the T2 modem fixture; the host ignores it.</param>
public sealed record NodeConfig(uint LocalAddress, CsmaConfig Csma, ModemFixture Modem)
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

    /// <summary>dts.*: DTS settings.</summary>
    public DtsConfig Dts { get; init; } = new(DtsConfig.MaxSegmentSizeLimit, DtsConfig.MaxTransmissionLimitMs);

    /// <summary>drc.*: data rate settings.</summary>
    public DrcConfig Drc { get; init; } = new(new Speed(2, Interleaver.Long));

    /// <summary>cas.*: CAS-1 linking settings, none with a default.</summary>
    public CasConfig Cas { get; init; } = CasConfig.Unset;
}

/// <summary>
/// cas.* settings for CAS-1 link establishment (P4-1). None has a default: the standard gives no
/// values (core-architecture.md 10 item 11), so each is null until configured and throws
/// <see cref="MissingSettingException"/> where the CAS needs it.
/// </summary>
/// <param name="MaxLinks">cas.max_links: the active physical link limit, 1 or more (B-0193, B-0209, B-0313, B-0402).</param>
/// <param name="LinkRequestTimeoutMs">cas.link_request_timeout_ms: the Caller's wait for an answer, from the end of the transmission carrying the request (B-0203, B-0204, B-0400).</param>
/// <param name="LinkRequestRetries">cas.link_request_retries: repeats of an unanswered request, so 1 + retries sends (B-0222, B-0407).</param>
/// <param name="FirstDataTimeoutMs">cas.first_data_timeout_ms: the Called node's wait for the first DATA C_PDU (B-0215, B-0216).</param>
public sealed record CasConfig(int? MaxLinks, int? LinkRequestTimeoutMs, int? LinkRequestRetries, int? FirstDataTimeoutMs)
{
    /// <summary>Every setting null.</summary>
    public static CasConfig Unset { get; } = new(null, null, null, null);

    /// <summary>cas.max_links, or throws when unset.</summary>
    public int RequireMaxLinks() => MaxLinks ?? throw new MissingSettingException("cas.max_links");

    /// <summary>cas.link_request_timeout_ms, or throws when unset.</summary>
    public int RequireLinkRequestTimeoutMs() => LinkRequestTimeoutMs ?? throw new MissingSettingException("cas.link_request_timeout_ms");

    /// <summary>cas.link_request_retries, or throws when unset.</summary>
    public int RequireLinkRequestRetries() => LinkRequestRetries ?? throw new MissingSettingException("cas.link_request_retries");

    /// <summary>cas.first_data_timeout_ms, or throws when unset.</summary>
    public int RequireFirstDataTimeoutMs() => FirstDataTimeoutMs ?? throw new MissingSettingException("cas.first_data_timeout_ms");
}

/// <summary>dts.* settings.</summary>
/// <param name="MaxSegmentSize">
/// dts.max_segment_size: the largest C_PDU segment a new D_PDU carries, 1 to 1023 bytes
/// (C-0503, C-0523, C-0555). Read when a C_PDU is segmented, so a change applies to C_PDUs
/// segmented after it (C-0549, C-0550; core-architecture.md 7.3).
/// </param>
/// <param name="MaxTransmissionMs">
/// dts.max_transmission_ms: the configured maximum transmission length, 1 to 127500 ms
/// (K-0094, K-0095). Never applied so as to block a single D_PDU (C-0551, C-0878).
/// </param>
/// <param name="MaxWindow">
/// dts.max_window: the maximum allowable transmit window size, 1 to 32768 D_PDUs for the 16-bit
/// family (C-0772, C-0773, C-0774). Read whenever a new D_PDU is placed (C-0775).
/// </param>
/// <param name="AckWaitMs">
/// dts.ack_wait_ms: how long after the end of a transmission carrying ARQ data to a peer the DTS
/// waits for an acknowledgement before it treats the unacknowledged D_PDUs as failed; no default
/// (C-0911, C-1120). Null until configured; read when <c>dts.ack_wait</c> starts.
/// </param>
public sealed record DtsConfig(int MaxSegmentSize, int MaxTransmissionMs, int MaxWindow = DtsConfig.MaxWindowLimit, int? AckWaitMs = null)
{
    /// <summary>The maximum window size of the 16-bit family (C-0771).</summary>
    public const int MaxWindowLimit = 32768;

    /// <summary>dts.ack_wait_ms, or throws when unset.</summary>
    public int RequireAckWaitMs() => AckWaitMs ?? throw new MissingSettingException("dts.ack_wait_ms");

    /// <summary>The largest segment size the standard allows (C-0503, C-0523).</summary>
    public const int MaxSegmentSizeLimit = 1023;

    /// <summary>The longest single-channel transmission, the 255 half seconds of EOT (C-0106, K-0094).</summary>
    public const int MaxTransmissionLimitMs = 127500;
}

/// <summary>A waveform and interleaver pair, the transmission parameters the planner chooses (C-0894).</summary>
/// <param name="Wn">Waveform number.</param>
/// <param name="Interleaver">Interleaver.</param>
public sealed record Speed(int Wn, Interleaver Interleaver);

/// <summary>drc.* settings.</summary>
/// <param name="DefaultSpeed">
/// drc.default_speed: the waveform and interleaver every transmission uses until rate selection
/// arrives in phase 7 (plan 6.2; core-architecture.md 5.3).
/// </param>
public sealed record DrcConfig(Speed DefaultSpeed);

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
