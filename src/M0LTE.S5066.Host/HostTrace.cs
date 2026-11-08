using System.Globalization;
using System.Text;
using M0LTE.S5066.Core;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host;

/// <summary>
/// The per-burst trace (issue #86): one ASCII line per D_PDU sent or received, timer start, expiry
/// and cancel, link and internal state change, and burst with its planned and actual timing. Off
/// unless a sink is given (<see cref="NodeHostOptions.Trace"/>). Every line starts with the host
/// time in ms and how late the loop was in handling it, when that is known.
/// </summary>
/// <remarks>
/// It runs on the host's loop only, so it needs no lock. It decodes what goes out and comes in
/// with its own copy of the D_PDU codec; the core is never asked.
/// </remarks>
internal sealed class HostTrace(Action<string> sink, IModemPort modem)
{
    private readonly List<byte> _rx = [];
    private long _txStart;
    private long? _txPlannedEnd;
    private int _rxBlocks;

    /// <summary>The core's own diagnostic lines, stamped with the input being handled.</summary>
    public void CoreLine(long t, string line) => Write(t, null, line);

    /// <summary>An input about to be handled at host time <paramref name="now"/>.</summary>
    public void Input(CoreInput input, long now)
    {
        long late = now - input.T;
        switch (input)
        {
            case TimerExpired e:
                Write(input.T, late, $"timer fired {e.Name}");
                break;
            case M0LTE.S5066.Core.Runtime.BurstStarted:
                _rx.Clear();
                _rxBlocks = 0;
                Write(input.T, late, "rx burst start");
                break;
            case BytesReceived b:
                _rxBlocks++;
                _rx.AddRange(b.Bytes.Span);
                DecodeRx(input.T, late, final: false);
                break;
            case M0LTE.S5066.Core.Runtime.BurstEnded b:
                DecodeRx(input.T, late, final: true);
                Write(input.T, late, $"rx burst end {b.Reason} wn={b.Wn} il={InterleaverNames.ToName(b.Interleaver)} blocks={b.Blocks} snr={(b.SnrTenthDb is int s ? (s / 10.0).ToString("0.0", CultureInfo.InvariantCulture) : "-")}");
                _rx.Clear();
                break;
            case M0LTE.S5066.Core.Runtime.TransmitDone:
                Write(input.T, late, _txPlannedEnd is long p
                    ? $"tx done planned_end={p} actual_end={input.T} slip={input.T - p} ms (started {_txStart})"
                    : "tx done (no burst planned)");
                _txPlannedEnd = null;
                break;
            case ChannelBusy c:
                Write(input.T, late, $"channel busy {c.Busy}");
                break;
            case SisConnect c:
                Write(input.T, late, $"sis connect conn={c.Conn}");
                break;
            case SisDisconnect d:
                Write(input.T, late, $"sis disconnect conn={d.Conn}");
                break;
            case SisBytes sb:
                Write(input.T, late, $"sis in conn={sb.Conn} bytes={sb.Bytes.Length}");
                break;
            case ConfigInput:
                Write(input.T, late, "config change");
                break;
        }
    }

    /// <summary>An output of the input handled at <paramref name="t"/>.</summary>
    public void Output(CoreOutput output, long t)
    {
        switch (output)
        {
            case TimerStart s:
                Write(t, null, $"timer start {s.Name} {s.Ms} ms (due {t + s.Ms})");
                break;
            case TimerCancel c:
                Write(t, null, $"timer cancel {c.Name}");
                break;
            case Transmit x:
                BurstPlan plan = modem.PlanBurst(x.Wn, x.Interleaver, x.Bytes.Length);
                _txStart = t;
                _txPlannedEnd = t + plan.DurationMs;
                Write(t, null, $"tx burst bytes={x.Bytes.Length} wn={x.Wn} il={InterleaverNames.ToName(x.Interleaver)} k={x.K} eom={x.Eom} blocks={plan.Blocks} planned={plan.DurationMs} ms (end {t + plan.DurationMs})");
                foreach (string d in DescribeAll(x.Bytes.Span))
                {
                    Write(t, null, $"tx {d}");
                }

                break;
            case LinkState l:
                Write(t, null, $"link {DpduAddress.ToDotted(l.Peer)} {l.State}");
                break;
            case SisBytesOut b:
                Write(t, null, $"sis out conn={b.Conn} bytes={b.Bytes.Length}");
                break;
            case SisClose c:
                Write(t, null, $"sis close conn={c.Conn}");
                break;
        }
    }

    /// <summary>A modem adapter line (modulation and playout timing).</summary>
    public void Modem(long t, string line) => Write(t, null, $"modem {line}");

    /// <summary>Describes each D_PDU in a byte stream, resyncing a byte at a time past anything that does not decode.</summary>
    internal static List<string> DescribeAll(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        int pos = 0;
        int skipped = 0;
        while (pos < bytes.Length)
        {
            DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(bytes[pos..], PeerEdition.Ed4);
            if (r.IsSuccess)
            {
                if (skipped > 0)
                {
                    lines.Add($"skipped {skipped} bytes");
                    skipped = 0;
                }

                lines.Add(Describe(r.Value!, r.Consumed));
                pos += r.Consumed;
            }
            else if (r.Error == PduErrorKind.Truncated)
            {
                break;
            }
            else
            {
                pos++;
                skipped++;
            }
        }

        if (skipped > 0 || pos < bytes.Length)
        {
            lines.Add($"undecoded {skipped + (bytes.Length - pos)} bytes");
        }

        return lines;
    }

    /// <summary>One D_PDU as a line: type, addresses, EOT and the type's sequence fields.</summary>
    internal static string Describe(Dpdu d, int size)
    {
        var sb = new StringBuilder();
        DpduHeader h = d.Header;
        sb.Append(CultureInfo.InvariantCulture, $"{Name(d)} {DpduAddress.ToDotted(h.Src)}>{DpduAddress.ToDotted(h.Dst)} eot={h.Eot} size={size}");
        switch (d)
        {
            case DataOnly x:
                sb.Append(CultureInfo.InvariantCulture, $" fsn={x.TxFsn} seg={x.Segment.Length}{Flags(x.Flags)}{Crc(x.SegmentCrcOk)}");
                break;
            case DataAck x:
                sb.Append(CultureInfo.InvariantCulture, $" fsn={x.TxFsn} seg={x.Segment.Length}{Flags(x.Flags)} rx_lwe={x.RxLwe} enc={x.Encoding} acks={x.SelectiveAcks}{Crc(x.SegmentCrcOk)}");
                break;
            case AckOnly x:
                sb.Append(CultureInfo.InvariantCulture, $" rx_lwe={x.RxLwe} enc={x.Encoding} off={x.Offset} acks={x.AckData}");
                break;
            case Ed3DataOnly x:
                sb.Append(CultureInfo.InvariantCulture, $" fsn={x.TxFsn} seg={x.Segment.Length}{Flags(x.Flags)}{Crc(x.SegmentCrcOk)}");
                break;
            case Ed3DataAck x:
                sb.Append(CultureInfo.InvariantCulture, $" fsn={x.TxFsn} seg={x.Segment.Length}{Flags(x.Flags)} rx_lwe={x.RxLwe} acks={x.SelectiveAcks}{Crc(x.SegmentCrcOk)}");
                break;
            case Ed3AckOnly x:
                sb.Append(CultureInfo.InvariantCulture, $" rx_lwe={x.RxLwe} acks={x.SelectiveAcks}");
                break;
            case ResetWinResync x:
                sb.Append(CultureInfo.InvariantCulture, $" full={x.Flags.FullResetCmnd} tx_rqst={x.Flags.ResetTxWinRqst} rx_cmnd={x.Flags.ResetRxWinCmnd} ack={x.Flags.ResetAck} new_rx_lwe={x.NewRxLwe} id={x.ResetFrameId}");
                break;
            case Ed3ResetWinResync x:
                sb.Append(CultureInfo.InvariantCulture, $" full={x.Flags.FullResetCmnd} tx_rqst={x.Flags.ResetTxWinRqst} rx_cmnd={x.Flags.ResetRxWinCmnd} ack={x.Flags.ResetAck} new_rx_lwe={x.NewRxLwe} id={x.ResetFrameId}");
                break;
            case NonArqData x:
                sb.Append(CultureInfo.InvariantCulture, $" cpdu_id={x.Fields.CPduId} size={x.Fields.CPduSize} off={x.Fields.SegmentOffset} seg={x.Segment.Length}{Crc(x.SegmentCrcOk)}");
                break;
            case ExpeditedNonArqData x:
                sb.Append(CultureInfo.InvariantCulture, $" cpdu_id={x.Fields.CPduId} size={x.Fields.CPduSize} off={x.Fields.SegmentOffset} seg={x.Segment.Length}{Crc(x.SegmentCrcOk)}");
                break;
            case ExpeditedDataOnly x:
                sb.Append(CultureInfo.InvariantCulture, $" fsn={x.TxFsn} cpdu_id={x.CPduId} seg={x.Segment.Length}{Crc(x.SegmentCrcOk)}");
                break;
            case ExpeditedAckOnly x:
                sb.Append(CultureInfo.InvariantCulture, $" rx_lwe={x.RxLwe} acks={x.SelectiveAcks}");
                break;
            case Management x:
                sb.Append(CultureInfo.InvariantCulture, $" id={x.ManagementFrameId} ack={x.Ack} valid={x.ValidMessage}");
                break;
            case Warning x:
                sb.Append(CultureInfo.InvariantCulture, $" rx_type={x.ReceivedFrameType} reason={x.Reason}");
                break;
        }

        return sb.ToString();
    }

    private static string Name(Dpdu d) => d switch
    {
        DataOnly => "DATA-ONLY",
        AckOnly => "ACK-ONLY",
        DataAck => "DATA-ACK",
        ResetWinResync => "RESET-WIN-RESYNC",
        Ed3DataOnly => "ED3-DATA-ONLY",
        Ed3AckOnly => "ED3-ACK-ONLY",
        Ed3DataAck => "ED3-DATA-ACK",
        Ed3ResetWinResync => "ED3-RESET-WIN-RESYNC",
        ExpeditedDataOnly => "EXP-DATA-ONLY",
        ExpeditedAckOnly => "EXP-ACK-ONLY",
        Management => "MANAGEMENT",
        NonArqData => "NON-ARQ",
        ExpeditedNonArqData => "EXP-NON-ARQ",
        Warning => "WARNING",
        Padding => "PADDING",
        _ => $"TYPE-{d.Type}",
    };

    private static string Flags(DataFlags f) =>
        (f.CPduStart ? " start" : string.Empty) + (f.CPduEnd ? " end" : string.Empty)
        + (f.TxWinLwe ? " lwe" : string.Empty) + (f.TxWinUwe ? " uwe" : string.Empty) + (f.DropCPdu ? " drop" : string.Empty);

    private static string Crc(bool ok) => ok ? string.Empty : " BAD-CRC";

    private void DecodeRx(long t, long late, bool final)
    {
        // Decode whole D_PDUs as they complete; keep a partial one for the next block.
        byte[] buf = [.. _rx];
        int pos = 0;
        while (pos < buf.Length)
        {
            DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(buf.AsSpan(pos), PeerEdition.Ed4);
            if (r.IsSuccess)
            {
                Write(t, late, $"rx {Describe(r.Value!, r.Consumed)} (block {_rxBlocks})");
                pos += r.Consumed;
            }
            else if (r.Error == PduErrorKind.Truncated && !final)
            {
                break;
            }
            else
            {
                pos++;
            }
        }

        _rx.RemoveRange(0, Math.Min(pos, _rx.Count));
    }

    private void Write(long t, long? late, string line) =>
        sink(late is long l && l > 0 ? $"trace {t} +{l} {line}" : $"trace {t} {line}");
}
