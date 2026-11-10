using System.Globalization;
using System.Text;
using System.Text.Json;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.TestClient.Monitor;

/// <summary>
/// Turns sample numbers into times. A file is timed from its start (<c>+00:01:02.345</c>); live
/// audio in UTC, anchored at each record to the wall clock and the samples taken so far, so a gap
/// in the audio (a UDP sender that stopped) does not skew later times.
/// </summary>
internal sealed class MonitorClock(bool live, Func<long> samplesProcessed, Func<DateTime>? now = null)
{
    private const double Rate = 9600.0;
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public bool Live => live;

    /// <summary>Seconds from the start of the audio.</summary>
    public static double Seconds(long sample) => sample / Rate;

    /// <summary>The UTC time of a sample, for live audio only.</summary>
    public DateTime Utc(long sample) => _now().AddSeconds(-(samplesProcessed() - sample) / Rate);

    /// <summary>The time column of a text line.</summary>
    public string Text(long sample)
    {
        if (live)
        {
            return Utc(sample).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        var t = TimeSpan.FromSeconds(Seconds(sample));
        return "+" + t.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The monitor's output: one ASCII line per D_PDU and per burst start and end on a text writer,
/// and optionally the same records as JSON lines for later analysis (docs/s5066-test.md).
/// </summary>
internal sealed class MonitorOutput(TextWriter? text, TextWriter? json, MonitorClock clock) : IMonitorSink
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public void BurstStarted(MonitorBurst b)
    {
        text?.WriteLine(Inv($"{clock.Text(b.StartSample)} BURST {b.Number} start {b.Waveform} K{b.K} snr {Db(b.PreambleSnrDb)} cfo {b.CfoHz:+0.0;-0.0}Hz"));
        Json(b.StartSample, new()
        {
            ["kind"] = "burst_start",
            ["burst"] = b.Number,
            ["wn"] = b.Wn,
            ["interleaver"] = b.Interleaver,
            ["k"] = b.K,
            ["preamble_snr_db"] = Round(b.PreambleSnrDb),
            ["cfo_hz"] = Round(b.CfoHz),
        });
    }

    public void Dpdu(MonitorDpdu d) => text?.WriteLine(DpduLine(d, clock.Text(d.Sample)));

    public void BurstEnded(MonitorBurstEnd e)
    {
        MonitorBurst b = e.Burst;
        double seconds = MonitorClock.Seconds(e.EndSample - b.StartSample);
        text?.WriteLine(Inv($"{clock.Text(e.EndSample)} BURST {b.Number} end {e.Reason} {seconds:0.00}s {e.Blocks} blocks snr {Db(e.SnrDb)}: {e.Dpdus} D_PDUs, {e.DataChecked} with data ({e.DataBad} CRC bad), {e.Cut} cut, {e.BadHeaders} bad headers"));
        Json(e.EndSample, new()
        {
            ["kind"] = "burst_end",
            ["burst"] = b.Number,
            ["reason"] = e.Reason,
            ["duration_s"] = Math.Round(seconds, 3),
            ["blocks"] = e.Blocks,
            ["snr_db"] = Round(e.SnrDb),
            ["dpdus"] = e.Dpdus,
            ["data_checked"] = e.DataChecked,
            ["data_bad"] = e.DataBad,
            ["cut"] = e.Cut,
            ["bad_headers"] = e.BadHeaders,
        });
    }

    /// <summary>The summary per source and destination, at the end of a run.</summary>
    public void Summary(MonitorDecoder m)
    {
        text?.WriteLine(Inv($"SUMMARY {m.Bursts} bursts, {m.Links.Count} links"));
        foreach ((MonitorLinkKey k, MonitorLink l) in m.Links)
        {
            text?.WriteLine(Inv($"LINK {Address(k.Src)} > {Address(k.Dst)}{(k.Group ? " (group)" : "")}: {l.Bursts} bursts, {l.Dpdus} D_PDUs, {l.Data} data ({l.DataBad} CRC bad, {l.DataBytes} bytes good), {l.Acks} acks, {l.Cut} cut; last {l.Waveform} snr {Db(l.SnrDb)}"));
            Json(l.LastSample, new()
            {
                ["kind"] = "link",
                ["src"] = Address(k.Src),
                ["dst"] = Address(k.Dst),
                ["group"] = k.Group,
                ["bursts"] = l.Bursts,
                ["dpdus"] = l.Dpdus,
                ["data"] = l.Data,
                ["data_bad"] = l.DataBad,
                ["data_bytes"] = l.DataBytes,
                ["acks"] = l.Acks,
                ["cut"] = l.Cut,
                ["first_t"] = Math.Round(MonitorClock.Seconds(l.FirstSample), 3),
                ["waveform"] = l.Waveform,
                ["snr_db"] = Round(l.SnrDb),
            });
        }
    }

    /// <summary>One D_PDU as a line of ASCII. Also writes its JSON line.</summary>
    internal string DpduLine(MonitorDpdu d, string time)
    {
        DpduHeader h = d.Header.Header;
        var fields = new Dictionary<string, object?>
        {
            ["kind"] = "dpdu",
            ["burst"] = d.Burst.Number,
            ["wn"] = d.Burst.Wn,
            ["interleaver"] = d.Burst.Interleaver,
            ["snr_db"] = Round(d.SnrDb),
            ["type"] = d.Header.Type,
            ["name"] = TypeName(d.Header.Type),
            ["src"] = Address(h.Src),
            ["dst"] = Address(h.Dst),
            ["group"] = d.Header.GroupAddress,
            ["eow"] = h.Eow,
            ["eow_text"] = EowText(h.Eow),
            ["eot"] = h.Eot,
            ["bytes"] = d.Bytes,
            ["data_len"] = d.Pdu is null ? null : MonitorDecoder.DataLength(d.Pdu),
            ["header_crc"] = "ok",
            ["data_crc"] = d.Pdu is null ? null : MonitorDecoder.DataCrc(d.Pdu) switch { true => "ok", false => "bad", null => null },
            ["status"] = d.Status,
        };

        var detail = new StringBuilder();
        if (d.Pdu is not null)
        {
            Detail(d.Pdu, detail, fields);
        }

        Json(d.Sample, fields);
        string dst = Address(h.Dst) + (d.Header.GroupAddress ? "(grp)" : "");
        string len = d.Pdu is null ? Inv($"{d.Bytes}B") : Inv($"len={MonitorDecoder.DataLength(d.Pdu)}");
        string crc = d.Status switch
        {
            "ok" => d.Pdu is not null && MonitorDecoder.DataCrc(d.Pdu) is null ? "crc=hdr-ok" : "crc=ok",
            "bad" => "crc=BAD",
            "cut" => "crc=CUT",
            _ => "crc=INVALID",
        };
        return Inv($"{time} {d.Burst.Waveform} {Db(d.SnrDb)} {Address(h.Src)} > {dst} {TypeName(d.Header.Type)}{detail} eow={EowText(h.Eow)} eot={h.Eot}({h.Eot * 0.5:0.0}s) {len} {crc}");
    }

    /// <summary>The short name of a D_PDU type [Ed4 Table C-2, p.C-8].</summary>
    internal static string TypeName(int type) => type switch
    {
        0 => "ED3-DATA-ONLY",
        1 => "ED3-ACK-ONLY",
        2 => "ED3-DATA-ACK",
        3 => "ED3-RESET",
        4 => "EXP-DATA-ONLY",
        5 => "EXP-ACK-ONLY",
        6 => "MANAGEMENT",
        7 => "NON-ARQ",
        8 => "EXP-NON-ARQ",
        9 => "DATA-ONLY",
        10 => "ACK-ONLY",
        11 => "DATA-ACK",
        12 => "RESET",
        13 => "EXTENSION",
        14 => "PADDING",
        15 => "WARNING",
        _ => Inv($"TYPE-{type}"),
    };

    /// <summary>An address as w.x.y.z.</summary>
    internal static string Address(uint a) => Inv($"{(a >> 24) & 15}.{(a >> 16) & 255}.{(a >> 8) & 255}.{a & 255}");

    /// <summary>A 12-bit EOW as a short phrase [Ed4 C.6, Table C-4, p.C-66].</summary>
    internal static string EowText(int value) => EowCodec.Decode(value) switch
    {
        EmptyEow => "-",
        Ed3BasicRateEow e => Inv($"ed3-rate:{e.DataRate}/{e.Interleaving}/{e.OtherParameters}"),
        UnrecognizedTypeErrorEow e => Inv($"unknown-eow:{e.UnrecognizedEowType}"),
        Ed3CapabilityEow => Inv($"ed3-cap:{value & 255:x2}"),
        MaxSpeedEow e => "max:" + Speed(e.Value),
        LowSpeedEow e => "low:" + Speed(e.Value),
        MaxSegmentEow e => Inv($"max-seg:{e.Value}"),
        SenderApproachEow e => Inv($"approach:{e.Strategy}{(e.Fixed ? ",fixed" : "")}{(e.Eow12Needed ? ",eow12" : "")}"),
        SpeedUsedEow e => "used:" + Speed(e.Value),
        CapabilityEow e => (e.Ed4 ? "cap:ed4" : "cap:ed3") + (e.Duplex ? ",duplex" : ""),
        _ => Inv($"{value >> 8}:{value & 255:x2}"),
    };

    private static string Speed(SpeedInterleaver s)
    {
        string il = s.Interleaver switch
        {
            0 => "-",
            1 => "US",
            2 => "VS",
            3 => "S",
            4 => "M",
            5 => "L",
            6 => "VL",
            _ => "?",
        };
        return Inv($"WN{s.Speed}/{il}");
    }

    private static void Detail(Dpdu pdu, StringBuilder s, Dictionary<string, object?> f)
    {
        switch (pdu)
        {
            case Ed3DataOnly p:
                Seq(s, f, "fsn", p.TxFsn);
                Flags(s, f, p.Flags);
                break;
            case Ed3AckOnly p:
                Seq(s, f, "lwe", p.RxLwe);
                Acks(s, f, p.SelectiveAcks.Length);
                break;
            case Ed3DataAck p:
                Seq(s, f, "fsn", p.TxFsn);
                Seq(s, f, "lwe", p.RxLwe);
                Acks(s, f, p.SelectiveAcks.Length);
                Flags(s, f, p.Flags);
                break;
            case Ed3ResetWinResync p:
                Reset(s, f, p.Flags, p.NewRxLwe, p.ResetFrameId);
                break;
            case ExpeditedDataOnly p:
                Seq(s, f, "fsn", p.TxFsn);
                Seq(s, f, "cpdu_id", p.CPduId);
                f["start"] = p.CPduStart;
                f["end"] = p.CPduEnd;
                s.Append(" flags=").Append(p.CPduStart ? "S" : "").Append(p.CPduEnd ? "E" : "").Append(!p.CPduStart && !p.CPduEnd ? "-" : "");
                break;
            case ExpeditedAckOnly p:
                Seq(s, f, "lwe", p.RxLwe);
                Acks(s, f, p.SelectiveAcks.Length);
                break;
            case Management p:
                Seq(s, f, "id", p.ManagementFrameId);
                f["ack"] = p.Ack;
                f["valid"] = p.ValidMessage;
                f["extended"] = p.ExtendedMessage;
                s.Append(" flags=").Append(p.Ack ? "A" : "").Append(p.ValidMessage ? "V" : "").Append(p.ExtendedMessage ? "X" : "").Append(!p.Ack && !p.ValidMessage && !p.ExtendedMessage ? "-" : "");
                break;
            case NonArqData p:
                NonArq(s, f, p.Fields);
                break;
            case ExpeditedNonArqData p:
                NonArq(s, f, p.Fields);
                break;
            case DataOnly p:
                Seq(s, f, "fsn", p.TxFsn);
                Flags(s, f, p.Flags);
                break;
            case AckOnly p:
                Seq(s, f, "lwe", p.RxLwe);
                Seq(s, f, "ack_offset", p.Offset);
                f["ack_encoding"] = p.Encoding == 0 ? "bitmap" : "rle";
                f["ack_bytes"] = p.AckData.Length;
                s.Append(Inv($" acks={(p.Encoding == 0 ? "bitmap" : "rle")}:{p.AckData.Length}B"));
                break;
            case DataAck p:
                Seq(s, f, "fsn", p.TxFsn);
                Seq(s, f, "lwe", p.RxLwe);
                f["ack_encoding"] = p.Encoding == 0 ? "bitmap" : "rle";
                Acks(s, f, p.SelectiveAcks.Length);
                Flags(s, f, p.Flags);
                break;
            case ResetWinResync p:
                Reset(s, f, p.Flags, p.NewRxLwe, p.ResetFrameId);
                break;
            case Extension p:
                Seq(s, f, "ext_type", p.ExtendedType);
                break;
            case Warning p:
                Seq(s, f, "rx_type", p.ReceivedFrameType);
                Seq(s, f, "reason", p.Reason);
                break;
        }
    }

    private static void Seq(StringBuilder s, Dictionary<string, object?> f, string name, int value)
    {
        f[name] = value;
        s.Append(Inv($" {name.Replace('_', '-')}={value}"));
    }

    private static void Acks(StringBuilder s, Dictionary<string, object?> f, int bytes)
    {
        f["ack_bytes"] = bytes;
        s.Append(Inv($" acks={bytes}B"));
    }

    private static void Flags(StringBuilder s, Dictionary<string, object?> f, DataFlags flags)
    {
        f["start"] = flags.CPduStart;
        f["end"] = flags.CPduEnd;
        f["in_order"] = flags.DeliverInOrder;
        f["drop"] = flags.DropCPdu;
        f["tx_uwe"] = flags.TxWinUwe;
        f["tx_lwe"] = flags.TxWinLwe;
        string letters = (flags.CPduStart ? "S" : "") + (flags.CPduEnd ? "E" : "") + (flags.DeliverInOrder ? "I" : "") + (flags.DropCPdu ? "D" : "") + (flags.TxWinUwe ? "U" : "") + (flags.TxWinLwe ? "L" : "");
        s.Append(" flags=").Append(letters.Length == 0 ? "-" : letters);
    }

    private static void Reset(StringBuilder s, Dictionary<string, object?> f, ResetFlags flags, int newLwe, int id)
    {
        Seq(s, f, "new_lwe", newLwe);
        Seq(s, f, "id", id);
        f["full_reset"] = flags.FullResetCmnd;
        f["reset_tx_win"] = flags.ResetTxWinRqst;
        f["reset_rx_win"] = flags.ResetRxWinCmnd;
        f["reset_ack"] = flags.ResetAck;
        string letters = (flags.FullResetCmnd ? "F" : "") + (flags.ResetTxWinRqst ? "T" : "") + (flags.ResetRxWinCmnd ? "R" : "") + (flags.ResetAck ? "A" : "");
        s.Append(" flags=").Append(letters.Length == 0 ? "-" : letters);
    }

    private static void NonArq(StringBuilder s, Dictionary<string, object?> f, NonArqFields n)
    {
        Seq(s, f, "cpdu_id", n.CPduId);
        Seq(s, f, "size", n.CPduSize);
        Seq(s, f, "offset", n.SegmentOffset);
        Seq(s, f, "rx_window", n.ReceptionWindow);
        f["in_order"] = n.DeliverInOrder;
    }

    private void Json(long sample, Dictionary<string, object?> fields)
    {
        if (json is null)
        {
            return;
        }

        var record = new Dictionary<string, object?> { ["t"] = Math.Round(MonitorClock.Seconds(sample), 3) };
        if (clock.Live)
        {
            record["utc"] = clock.Utc(sample).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        foreach ((string k, object? v) in fields)
        {
            record[k] = v;
        }

        json.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
    }

    private static double? Round(double? v) => v is double d ? Math.Round(d, 1) : null;

    private static string Db(double? v) => v is double d ? Inv($"{d:0.0}dB") : "-dB";

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
