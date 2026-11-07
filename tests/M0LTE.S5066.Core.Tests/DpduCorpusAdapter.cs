using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Converts between the corpus JSON shape (corpus-format.md sections 3.3 to 3.6) and the core's D_PDU,
/// EOW, address and speed byte types. The conversion lives here, not in the core (harness-contract.md
/// section 2).
/// </summary>
internal static class DpduCorpusAdapter
{
    public static string ErrorName(DecodeErrorKind k) => k switch
    {
        DecodeErrorKind.Truncated => "truncated",
        DecodeErrorKind.BadSync => "bad_sync",
        DecodeErrorKind.HeaderCrc => "header_crc",
        DecodeErrorKind.BadHeaderSize => "bad_header_size",
        DecodeErrorKind.BadAddressSize => "bad_address_size",
        DecodeErrorKind.TrailingBytes => "trailing_bytes",
        DecodeErrorKind.InvalidField => "invalid_field",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static PeerEdition Peer(string s) => s switch
    {
        "any" => PeerEdition.Any,
        "ed3" => PeerEdition.Ed3,
        "ed4" => PeerEdition.Ed4,
        _ => throw new ArgumentException("peer_edition " + s),
    };

    // ------------------------------------------------------------ D_PDU

    public static JsonObject ToJson(Dpdu pdu)
    {
        DpduHeader h = pdu.Header;
        var o = new JsonObject
        {
            ["type"] = pdu.Type,
            ["eow"] = h.Eow,
            ["eot"] = h.Eot,
            ["address_size"] = h.AddressSize,
            ["dst"] = DpduAddress.ToDotted(h.Dst),
            ["src"] = DpduAddress.ToDotted(h.Src),
        };
        var f = new JsonObject();
        void Flags(DataFlags d)
        {
            f["c_pdu_start"] = d.CPduStart;
            f["c_pdu_end"] = d.CPduEnd;
            f["deliver_in_order"] = d.DeliverInOrder;
            f["drop_c_pdu"] = d.DropCPdu;
            f["tx_win_uwe"] = d.TxWinUwe;
            f["tx_win_lwe"] = d.TxWinLwe;
        }

        void Reset(ResetFlags r)
        {
            f["full_reset_cmnd"] = r.FullResetCmnd;
            f["reset_tx_win_rqst"] = r.ResetTxWinRqst;
            f["reset_rx_win_cmnd"] = r.ResetRxWinCmnd;
            f["reset_ack"] = r.ResetAck;
        }

        void NonArq(NonArqFields n)
        {
            f["c_pdu_id"] = n.CPduId;
            f["deliver_in_order"] = n.DeliverInOrder;
            f["group_address"] = n.GroupAddress;
            f["c_pdu_size"] = n.CPduSize;
            f["c_pdu_segment_offset"] = n.SegmentOffset;
            f["c_pdu_reception_window"] = n.ReceptionWindow;
        }

        Octets? segment = null;
        bool segOk = false;
        switch (pdu)
        {
            case Ed3DataOnly p: Flags(p.Flags); f["tx_fsn"] = p.TxFsn; (segment, segOk) = (p.Segment, p.SegmentCrcOk); break;
            case DataOnly p: Flags(p.Flags); f["tx_fsn"] = p.TxFsn; (segment, segOk) = (p.Segment, p.SegmentCrcOk); break;
            case Ed3AckOnly p: f["rx_lwe"] = p.RxLwe; f["selective_acks"] = p.SelectiveAcks.ToString(); break;
            case ExpeditedAckOnly p: f["rx_lwe"] = p.RxLwe; f["selective_acks"] = p.SelectiveAcks.ToString(); break;
            case Ed3DataAck p:
                Flags(p.Flags);
                f["tx_fsn"] = p.TxFsn;
                f["rx_lwe"] = p.RxLwe;
                f["selective_acks"] = p.SelectiveAcks.ToString();
                (segment, segOk) = (p.Segment, p.SegmentCrcOk);
                break;
            case DataAck p:
                Flags(p.Flags);
                f["tx_fsn"] = p.TxFsn;
                f["rx_lwe"] = p.RxLwe;
                f["encoding"] = p.Encoding;
                f["selective_acks"] = p.SelectiveAcks.ToString();
                (segment, segOk) = (p.Segment, p.SegmentCrcOk);
                break;
            case AckOnly p:
                f["rx_lwe"] = p.RxLwe;
                f["encoding"] = p.Encoding;
                f["offset"] = p.Offset;
                f["ack_data"] = p.AckData.ToString();
                if (p.AckDataCrcOk is { } ok)
                {
                    f["ack_data_crc_ok"] = ok;
                }

                break;
            case Ed3ResetWinResync p: Reset(p.Flags); f["new_rx_lwe"] = p.NewRxLwe; f["reset_frame_id"] = p.ResetFrameId; break;
            case ResetWinResync p: Reset(p.Flags); f["new_rx_lwe"] = p.NewRxLwe; f["reset_frame_id"] = p.ResetFrameId; break;
            case ExpeditedDataOnly p:
                f["c_pdu_start"] = p.CPduStart;
                f["c_pdu_end"] = p.CPduEnd;
                f["c_pdu_id"] = p.CPduId;
                f["tx_fsn"] = p.TxFsn;
                (segment, segOk) = (p.Segment, p.SegmentCrcOk);
                break;
            case Management p:
                f["extended_message"] = p.ExtendedMessage;
                f["valid_message"] = p.ValidMessage;
                f["ack"] = p.Ack;
                f["management_frame_id"] = p.ManagementFrameId;
                f["extended_data"] = p.ExtendedData.ToString();
                break;
            case NonArqData p: NonArq(p.Fields); (segment, segOk) = (p.Segment, p.SegmentCrcOk); break;
            case ExpeditedNonArqData p: NonArq(p.Fields); (segment, segOk) = (p.Segment, p.SegmentCrcOk); break;
            case Extension p: f["extended_type"] = p.ExtendedType; f["extended_header"] = p.ExtendedHeader.ToString(); break;
            case Padding: break;
            case Warning p: f["received_frame_type"] = p.ReceivedFrameType; f["reason"] = p.Reason; break;
            case UndefinedTypeDpdu p: f["undefined"] = true; f["type_specific"] = p.TypeSpecific.ToString(); break;
            default: throw new NotSupportedException(pdu.GetType().Name);
        }

        o["fields"] = f;
        if (segment is not null)
        {
            o["segment"] = segment.ToString();
            o["segment_crc_ok"] = segOk;
        }

        return o;
    }

    public static Dpdu FromJson(JsonNode value)
    {
        JsonObject o = value.AsObject();
        JsonObject f = o["fields"]!.AsObject();
        int I(JsonNode n, string k) => n[k]!.GetValue<int>();
        bool B(string k) => f[k]!.GetValue<bool>();
        Octets X(string k) => new(Convert.FromHexString(f[k]!.GetValue<string>()));
        Octets Seg() => new(Convert.FromHexString(o["segment"]!.GetValue<string>()));
        bool SegOk() => o["segment_crc_ok"]!.GetValue<bool>();
        DpduAddress.TryParse(o["dst"]!.GetValue<string>(), out uint dst).Should().BeTrue();
        DpduAddress.TryParse(o["src"]!.GetValue<string>(), out uint src).Should().BeTrue();
        var h = new DpduHeader(I(o, "eow"), I(o, "eot"), I(o, "address_size"), dst, src);
        DataFlags Flags() => new(B("c_pdu_start"), B("c_pdu_end"), B("deliver_in_order"), B("drop_c_pdu"), B("tx_win_uwe"), B("tx_win_lwe"));
        ResetFlags Reset() => new(B("full_reset_cmnd"), B("reset_tx_win_rqst"), B("reset_rx_win_cmnd"), B("reset_ack"));
        NonArqFields NonArq() => new(I(f, "c_pdu_id"), B("deliver_in_order"), B("group_address"), I(f, "c_pdu_size"), I(f, "c_pdu_segment_offset"), I(f, "c_pdu_reception_window"));
        return I(o, "type") switch
        {
            0 => new Ed3DataOnly(h, Flags(), I(f, "tx_fsn"), Seg(), SegOk()),
            1 => new Ed3AckOnly(h, I(f, "rx_lwe"), X("selective_acks")),
            2 => new Ed3DataAck(h, Flags(), I(f, "tx_fsn"), I(f, "rx_lwe"), X("selective_acks"), Seg(), SegOk()),
            3 => new Ed3ResetWinResync(h, Reset(), I(f, "new_rx_lwe"), I(f, "reset_frame_id")),
            4 => new ExpeditedDataOnly(h, B("c_pdu_start"), B("c_pdu_end"), I(f, "c_pdu_id"), I(f, "tx_fsn"), Seg(), SegOk()),
            5 => new ExpeditedAckOnly(h, I(f, "rx_lwe"), X("selective_acks")),
            6 => new Management(h, B("extended_message"), B("valid_message"), B("ack"), I(f, "management_frame_id"), X("extended_data")),
            7 => new NonArqData(h, NonArq(), Seg(), SegOk()),
            8 => new ExpeditedNonArqData(h, NonArq(), Seg(), SegOk()),
            9 => new DataOnly(h, Flags(), I(f, "tx_fsn"), Seg(), SegOk()),
            10 => new AckOnly(h, I(f, "rx_lwe"), I(f, "encoding"), I(f, "offset"), X("ack_data"),
                f.ContainsKey("ack_data_crc_ok") ? B("ack_data_crc_ok") : null),
            11 => new DataAck(h, Flags(), I(f, "tx_fsn"), I(f, "rx_lwe"), I(f, "encoding"), X("selective_acks"), Seg(), SegOk()),
            12 => new ResetWinResync(h, Reset(), I(f, "new_rx_lwe"), I(f, "reset_frame_id")),
            13 => new Extension(h, I(f, "extended_type"), X("extended_header")),
            14 => new Padding(h),
            15 => new Warning(h, I(f, "received_frame_type"), I(f, "reason")),
            _ => throw new NotSupportedException("type"),
        };
    }

    // ------------------------------------------------------------ EOW

    public static JsonObject EowToJson(Eow e)
    {
        var f = new JsonObject();
        switch (e)
        {
            case EmptyEow: break;
            case Ed3BasicRateEow x: f["data_rate"] = x.DataRate; f["interleaving"] = x.Interleaving; f["other_parameters"] = x.OtherParameters; break;
            case UnrecognizedTypeErrorEow x: f["unrecognized_eow_type"] = x.UnrecognizedEowType; break;
            case Ed3CapabilityEow x:
                f["drc_capable"] = x.DrcCapable;
                f["stanag_4529"] = x.Stanag4529;
                f["ms110_75_2400"] = x.Ms110_75To2400;
                f["extended_rate"] = x.ExtendedRate;
                f["full_duplex"] = x.FullDuplex;
                f["split_frequency"] = x.SplitFrequency;
                f["non_arcs_ale"] = x.NonArcsAle;
                f["arcs"] = x.Arcs;
                break;
            case MaxSpeedEow x: Speed(f, x.Value); break;
            case LowSpeedEow x: Speed(f, x.Value); break;
            case SpeedUsedEow x: Speed(f, x.Value); break;
            case MaxSegmentEow x: f["max_segment"] = x.Value; break;
            case SenderApproachEow x: f["eow12_needed"] = x.Eow12Needed; f["strategy"] = x.Strategy; f["fixed"] = x.Fixed; break;
            case CapabilityEow x: f["duplex"] = x.Duplex; f["ed4"] = x.Ed4; break;
            case UnrecognisedEow x: f["contents"] = x.Contents; break;
            default: throw new NotSupportedException(e.GetType().Name);
        }

        return new JsonObject { ["type"] = e.Type, ["fields"] = f };
    }

    private static void Speed(JsonObject f, SpeedInterleaver s)
    {
        f["speed"] = s.Speed;
        f["interleaver"] = s.Interleaver;
    }

    public static Eow EowFromJson(JsonNode value)
    {
        JsonObject f = value["fields"]!.AsObject();
        int I(string k) => f[k]!.GetValue<int>();
        bool B(string k) => f[k]!.GetValue<bool>();
        SpeedInterleaver S() => new(I("interleaver"), I("speed"));
        return value["type"]!.GetValue<int>() switch
        {
            0 => new EmptyEow(),
            1 => new Ed3BasicRateEow(I("data_rate"), I("interleaving"), I("other_parameters")),
            3 => new UnrecognizedTypeErrorEow(I("unrecognized_eow_type")),
            4 => new Ed3CapabilityEow(B("drc_capable"), B("stanag_4529"), B("ms110_75_2400"), B("extended_rate"), B("full_duplex"), B("split_frequency"), B("non_arcs_ale"), B("arcs")),
            8 => new MaxSpeedEow(S()),
            9 => new LowSpeedEow(S()),
            10 => new MaxSegmentEow(I("max_segment")),
            11 => new SenderApproachEow(B("eow12_needed"), I("strategy"), B("fixed")),
            12 => new SpeedUsedEow(S()),
            15 => new CapabilityEow(B("duplex"), B("ed4")),
            int t => new UnrecognisedEow(t, I("contents")),
        };
    }
}
