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

    /// <summary>cas.*: CAS-1 link settings. The timers are derived when unset (<see cref="AnswerWaitMs"/>).</summary>
    public CasConfig Cas { get; init; } = CasConfig.Unset;

    /// <summary>
    /// The longest a peer that heard a transmission of ours and shares our settings takes to answer it
    /// (C-1120): the longer LBT wait, since it may have heard only part of our transmission (K-0067,
    /// K-0077), plus the longest contention wait (K-0080), then a transmission of the configured length
    /// (K-0094), by whose end the answer has arrived. Every derived wait is built from it.
    /// </summary>
    public long DerivedAnswerWaitMs() =>
        Math.Min(DtsConfig.MaxTransmissionLimitMs, Dts.MaxTransmissionMs)
        + Math.Max(Csma.LbtWaitDcdMs, Csma.LbtWaitVdcdMs) + ((long)Csma.NumContSlots * Csma.ContSlotWidthMs);

    /// <summary>
    /// The time allowed for the peer to answer one transmission: <c>dts.ack_wait_ms</c>, or when unset
    /// <see cref="DerivedAnswerWaitMs"/> (C-1120). Also the RESET/WIN-RESYNC wait (C-1172).
    /// </summary>
    public long AnswerWaitMs() => Dts.AckWaitMs ?? DerivedAnswerWaitMs();

    /// <summary>cas.link_request_timeout_ms, or when unset one answer wait (issue #49; interp B-0420).</summary>
    public long LinkRequestTimeoutMs() => Cas.LinkRequestTimeoutMs ?? AnswerWaitMs();

    /// <summary>cas.first_data_timeout_ms, or when unset three answer waits (issue #49; interp B-0420).</summary>
    public long FirstDataTimeoutMs() => Cas.FirstDataTimeoutMs ?? (3 * AnswerWaitMs());

    /// <summary>cas.break_timeout_ms, or when unset one answer wait (B-0283; issue #49; interp B-0420).</summary>
    public long BreakTimeoutMs() => Cas.BreakTimeoutMs ?? AnswerWaitMs();

    /// <summary>cas.clean_close_timeout_ms, or when unset three answer waits (B-0322; interp B-0420).</summary>
    public long CleanCloseTimeoutMs() => Cas.CleanCloseTimeoutMs ?? (3 * AnswerWaitMs());

    /// <summary>sis.soft_link_idle_ms, or when unset three answer waits (A-0011; interp A-0321).</summary>
    public long SoftLinkIdleMs() => Sis.SoftLinkIdleMs ?? (3 * AnswerWaitMs());

    /// <summary>sis.soft_link_min_hold_ms, or when unset three answer waits, the called node's wait for the first DATA C_PDU (A-0197; interp A-0321).</summary>
    public long SoftLinkMinHoldMs() => Sis.SoftLinkMinHoldMs ?? (3 * AnswerWaitMs());

    /// <summary>sis.soft_link_max_ms, or when unset six answer waits, twice the minimum hold (A-0014, A-0015; interp A-0321).</summary>
    public long SoftLinkMaxMs() => Sis.SoftLinkMaxMs ?? (6 * AnswerWaitMs());
}

/// <summary>
/// cas.* settings for CAS-1 links (P4-1, P5-1). The counts and the link limit have no default and
/// throw <see cref="MissingSettingException"/> where the CAS needs them. The timers, when null, are
/// derived from the answer wait (<see cref="NodeConfig.AnswerWaitMs"/>; issue #49, interp B-0420).
/// </summary>
/// <param name="MaxLinks">cas.max_links: the active physical link limit, 1 or more (B-0193, B-0209, B-0313, B-0402).</param>
/// <param name="LinkRequestTimeoutMs">cas.link_request_timeout_ms: the Caller's wait for an answer, from the end of the transmission carrying the request (B-0203, B-0204, B-0400).</param>
/// <param name="LinkRequestRetries">cas.link_request_retries: repeats of an unanswered request, so 1 + retries sends (B-0222, B-0407).</param>
/// <param name="FirstDataTimeoutMs">cas.first_data_timeout_ms: the Called node's wait for the first DATA C_PDU (B-0215, B-0216).</param>
/// <param name="BreakTimeoutMs">cas.break_timeout_ms: the Initiator's wait for PHYSICAL LINK BREAK CONFIRM, from the end of the transmission carrying the break (B-0283, B-0400).</param>
/// <param name="BreakRetries">cas.break_retries: repeats of an unanswered PHYSICAL LINK BREAK, so 1 + retries sends (B-0287, B-0288; interp B-0424).</param>
/// <param name="CleanCloseTimeoutMs">cas.clean_close_timeout_ms: the longest a close this node asked for waits for clean close state (B-0322).</param>
public sealed record CasConfig(int? MaxLinks, int? LinkRequestTimeoutMs, int? LinkRequestRetries, int? FirstDataTimeoutMs,
                               int? BreakTimeoutMs = null, int? BreakRetries = null, int? CleanCloseTimeoutMs = null)
{
    /// <summary>Every setting null.</summary>
    public static CasConfig Unset { get; } = new(null, null, null, null);

    /// <summary>cas.max_links, or throws when unset.</summary>
    public int RequireMaxLinks() => MaxLinks ?? throw new MissingSettingException("cas.max_links");

    /// <summary>cas.link_request_retries, or throws when unset.</summary>
    public int RequireLinkRequestRetries() => LinkRequestRetries ?? throw new MissingSettingException("cas.link_request_retries");

    /// <summary>cas.break_retries, or throws when unset.</summary>
    public int RequireBreakRetries() => BreakRetries ?? throw new MissingSettingException("cas.break_retries");
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
/// dts.max_window: the maximum allowable transmit window size, 1 to 32768 D_PDUs (C-0772, C-0773,
/// C-0774). On a link with the 8-bit family the family's maximum window, 128, caps it (C-0771,
/// interp C-1193). Read whenever a new D_PDU is placed (C-0775).
/// </param>
/// <param name="AckWaitMs">
/// dts.ack_wait_ms: how long after the end of a transmission carrying ARQ data to a peer the DTS
/// waits for an acknowledgement before it treats the unacknowledged D_PDUs as failed (C-0911,
/// C-1120). The standard gives no value. When null the wait is derived: the configured transmission
/// length plus the longer of the two LBT waits plus the longest contention wait, the longest a peer
/// sharing our settings takes to answer. Read when <c>dts.ack_wait</c> starts. An explicit value
/// below <c>csma.lbt_wait_dcd_ms</c> is refused at configuration load: a peer that heard only part of
/// our transmission may not answer before that wait (issue #49 (b); interp C-1252).
/// </param>
public sealed record DtsConfig(int MaxSegmentSize, int MaxTransmissionMs, int MaxWindow = DtsConfig.MaxWindowLimit, int? AckWaitMs = null)
{
    /// <summary>The maximum window size of the 16-bit family (C-0771).</summary>
    public const int MaxWindowLimit = 32768;

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
public sealed record DrcConfig(Speed DefaultSpeed)
{
    /// <summary>
    /// drc.table: the rate selection calibration (corpus-format.md 6.1; core-architecture.md 9, phase
    /// 7, design shape items 5 and 6). Empty by default: no calibration, so no recommendations and
    /// fixed speed at <see cref="DefaultSpeed"/>.
    /// </summary>
    public DrcTable Table { get; init; } = DrcTable.Empty;
}

/// <summary>
/// drc.table (corpus-format.md 6.1): calibration data, not an operator setting. Integers only
/// (core-architecture.md 6 rule 1). The constants are null in <c>defaults.json</c>; a null one the
/// core needs throws <see cref="MissingSettingException"/> where it is read.
/// </summary>
/// <param name="Ladder">The rungs, slowest first; empty means no calibration.</param>
/// <param name="Thresholds">Per measured rung, the estimate thresholds for each ladder position.</param>
/// <param name="SmoothingWeight">smoothing_weight, 1 to 16, in sixteenths.</param>
/// <param name="CleanRun">clean_run: clean bursts in a row before a move up, and before MAX-SEGMENT doubles.</param>
/// <param name="RxErrorLimitPpt">rx_error_limit_ppt: a burst D_PDU error rate above this caps its rung.</param>
/// <param name="RxCeilingInitial">rx_ceiling_initial: bursts a first cap lasts.</param>
/// <param name="RxCeilingCap">rx_ceiling_cap: the longest cap, in bursts.</param>
/// <param name="TxStepDownWaits">tx_step_down_waits (P7-7).</param>
/// <param name="TxCeilingInitial">tx_ceiling_initial (P7-7).</param>
/// <param name="TxCeilingCap">tx_ceiling_cap (P7-7).</param>
public sealed record DrcTable(
    IReadOnlyList<Speed> Ladder,
    IReadOnlyList<DrcThresholds> Thresholds,
    int? SmoothingWeight = null,
    int? CleanRun = null,
    int? RxErrorLimitPpt = null,
    int? RxCeilingInitial = null,
    int? RxCeilingCap = null,
    int? TxStepDownWaits = null,
    int? TxCeilingInitial = null,
    int? TxCeilingCap = null)
{
    /// <summary>The empty table of <c>defaults.json</c>.</summary>
    public static DrcTable Empty { get; } = new([], []);

    /// <summary>No calibration: an empty ladder.</summary>
    public bool IsEmpty => Ladder.Count == 0;

    /// <summary>The ladder position of a rung, or -1 when it is not on the ladder.</summary>
    public int PositionOf(Speed rung)
    {
        for (int i = 0; i < Ladder.Count; i++)
        {
            if (Ladder[i] == rung)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The thresholds measured on a rung, or null when the table has none for it.</summary>
    public DrcThresholds? ThresholdsOn(Speed rung)
    {
        foreach (DrcThresholds t in Thresholds)
        {
            if (t.Rung == rung)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>Reads a constant the core needs; null throws <see cref="MissingSettingException"/>.</summary>
    internal static int Need(int? value, string key) => value ?? throw new MissingSettingException("drc.table." + key);
}

/// <summary>One <c>drc.table.thresholds</c> entry (corpus-format.md 6.1).</summary>
/// <param name="Rung">The rung the estimate is measured on.</param>
/// <param name="Bulk">Per ladder position, the smoothed estimate in tenths of a dB at or above which that rung meets the bulk criterion, or null for never.</param>
/// <param name="Latency">The same for the latency criterion.</param>
public sealed record DrcThresholds(Speed Rung, IReadOnlyList<int?> Bulk, IReadOnlyList<int?> Latency);

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
/// <param name="SoftLinkIdleMs">
/// sis.soft_link_idle_ms: how long a soft link carries no data before the SIS closes it, 0 or more
/// (A-0011, A-0013; interp A-0320). Null: three answer waits (interp A-0321).
/// </param>
/// <param name="SoftLinkMinHoldMs">
/// sis.soft_link_min_hold_ms: the least time a called node keeps a soft link before closing it on
/// its own, 0 or more (A-0197). Null: three answer waits (interp A-0321).
/// </param>
/// <param name="SoftLinkMaxMs">
/// sis.soft_link_max_ms: the longest a soft link is kept while U_PDUs for another node wait for a
/// link, 1 or more (A-0014, A-0015; interp A-0322). Null: six answer waits (interp A-0321).
/// </param>
/// <param name="FragSize">
/// sis.frag_size: U_PDU_response_frag_size, the most bytes of the U_PDU an S_UNIDATA_REQUEST_CONFIRM,
/// S_UNIDATA_REQUEST_REJECTED, DATA DELIVERY CONFIRM or DATA DELIVERY FAIL copies, 0 or more
/// (A-0100 to A-0102, A-0112 to A-0114; interp A-0341). Null or absent: the whole U_PDU (issue #30).
/// </param>
/// <param name="ConfirmWaitMs">
/// sis.confirm_wait_ms: how long the SIS waits for the answer to a U_PDU that asked for node or client
/// delivery confirmation, from when it is handed to the CAS, 1 or more (A-0221, A-0226; interp A-0232).
/// Null: until the U_PDU's TTD (interp A-0342).
/// </param>
public sealed record SisConfig(int DefaultTtl, int? SoftLinkIdleMs = null, int? SoftLinkMinHoldMs = null, int? SoftLinkMaxMs = null,
                               int? FragSize = null, int? ConfirmWaitMs = null)
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
/// <param name="EomBytes">modem.eom_bytes: block space the end-of-message marker takes after the last byte, default 0 (interp C-1290).</param>
public sealed record ModemFixture(int? PreambleMs, int? FlushMs, IReadOnlyList<ModemWaveform> Waveforms, int EomBytes = 0);

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

    /// <summary>Creates the exception with its own message, for a host that refuses a file at load (issue #83).</summary>
    /// <param name="setting">The dotted key.</param>
    /// <param name="message">What is missing and what to do.</param>
    public MissingSettingException(string setting, string message)
        : base(setting, message)
    {
    }
}
