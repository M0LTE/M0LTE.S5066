using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// Authoring aid for P7-3 (core-architecture.md 9, phase 7 design shape item 8): re-derives the EOW
/// of every D_PDU this node transmits in an existing T2 trace from the rules alone, never from
/// the implementation. The node's knowledge of each peer's edition is followed through the trace
/// by the B-0408 reading (a received PHYSICAL LINK REQUEST records its Ed4 bit, unless its LINK bit
/// is set, when it is only rejected (B-0128); a PHYSICAL LINK
/// ACCEPTED answering our Ed4 request records its Ed4 bit; a WARNING about a type 8 answering our
/// Ed4 request records Ed3) and by the P7-3 reading of a received CAPABILITY (interp C-0055: Ed4
/// where CAS-1 has recorded nothing). A transmitted D_PDU to an individual peer recorded Ed4 whose
/// EOW is EMPTY gets CAPABILITY (C-0426); an owed UNRECOGNIZED-TYPE-ERROR, which is not EMPTY, is
/// left alone (C-1079). Only the two EOW bytes and the two header CRC bytes of a D_PDU change, and
/// they are patched in place, so nothing else can move.
/// </summary>
internal static class EowRederive
{
    private const int Capability = 0xF01;

    /// <summary>Rewrites the trace lines; returns the new lines and how many D_PDUs changed.</summary>
    public static (List<string> Lines, int Changed) Apply(IReadOnlyList<string> lines)
    {
        JsonObject header = JsonNode.Parse(lines[0])!.AsObject();
        uint local = ParseAddress((string)header["config"]!["local_address"]!);
        var editions = new Dictionary<uint, (bool Ed4, bool ByCas1)>();
        var calledEd4 = new Dictionary<uint, bool>();   // our latest PHYSICAL LINK REQUEST to the peer had Ed4 set
        var rx = new List<byte>();
        var output = new List<string> { lines[0] };
        int changed = 0;

        bool IsEd4(uint p) => editions.TryGetValue(p, out var e) && e.Ed4;
        PeerEdition DecodeAs(uint p) => editions.TryGetValue(p, out var e) && !e.Ed4 ? PeerEdition.Ed3 : PeerEdition.Ed4;

        void Received(Dpdu pdu)
        {
            DpduHeader h = pdu.Header;
            if (h.Dst != local)
            {
                return;
            }

            bool group = pdu is NonArqData { Fields.GroupAddress: true } or ExpeditedNonArqData { Fields.GroupAddress: true };
            if (group)
            {
                return;
            }

            if (EowCodec.Decode(h.Eow) is CapabilityEow { Ed4: true } && !(editions.TryGetValue(h.Src, out var rec) && rec.ByCas1))
            {
                editions[h.Src] = (true, false);
            }

            if (pdu is ExpeditedNonArqData x && x.Fields.SegmentOffset == 0 && CpduCodec.Decode(x.Segment.Span).Value is CPdu c)
            {
                switch (c)
                {
                    case PhysicalLinkRequest { Link: false } r:   // one with LINK set is rejected with reason 5 and nothing else (B-0128, B-0130 in order)
                        editions[h.Src] = (r.Ed4, true);
                        break;
                    case PhysicalLinkAccepted a when calledEd4.TryGetValue(h.Src, out bool asked) && asked:
                        editions[h.Src] = (a.Ed4, true);
                        calledEd4.Remove(h.Src);
                        break;
                }
            }
            else if (pdu is Warning { ReceivedFrameType: 8 } && calledEd4.TryGetValue(h.Src, out bool askedEd4) && askedEd4)
            {
                editions[h.Src] = (false, true);
                calledEd4.Remove(h.Src);
            }
        }

        foreach (string text in lines.Skip(1))
        {
            JsonObject line = JsonNode.Parse(text)!.AsObject();
            string? input = (string?)line["in"];
            if (input == "burst_started")
            {
                rx.Clear();
            }
            else if (input == "bytes_received")
            {
                rx.AddRange(Convert.FromHexString((string)line["bytes"]!));
                while (true)
                {
                    int sync = IndexOfSync(rx);
                    if (sync < 0)
                    {
                        rx.RemoveRange(0, Math.Max(0, rx.Count - 1));
                        break;
                    }

                    rx.RemoveRange(0, sync);
                    byte[] buf = rx.ToArray();
                    uint src = DpduCodec.TryPeekSource(buf, out uint s) ? s : 0;
                    DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(buf, DecodeAs(src));
                    if (r.IsSuccess)
                    {
                        Received(r.Value!);
                        rx.RemoveRange(0, r.Consumed);
                    }
                    else if (r.Error == PduErrorKind.Truncated)
                    {
                        break;
                    }
                    else
                    {
                        rx.RemoveAt(0);
                    }
                }
            }
            else if ((string?)line["out"] == "transmit")
            {
                byte[] bytes = Convert.FromHexString((string)line["bytes"]!);
                int off = 0;
                while (off + 1 < bytes.Length && bytes[off] == 0x90 && bytes[off + 1] == 0xeb)
                {
                    DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(bytes.AsSpan(off), PeerEdition.Ed4);
                    if (!r.IsSuccess)
                    {
                        throw new InvalidDataException($"undecodable transmitted D_PDU at {off}: {r.Error}");
                    }

                    Dpdu pdu = r.Value!;
                    DpduHeader h = pdu.Header;
                    bool group = pdu is NonArqData { Fields.GroupAddress: true } or ExpeditedNonArqData { Fields.GroupAddress: true };
                    if (pdu is ExpeditedNonArqData x && !group && x.Fields.SegmentOffset == 0
                        && CpduCodec.Decode(x.Segment.Span).Value is PhysicalLinkRequest req)
                    {
                        calledEd4[h.Dst] = req.Ed4;
                    }

                    if (!group && h.Eow == 0 && IsEd4(h.Dst))
                    {
                        int type = bytes[off + 2] >> 4;
                        bytes[off + 2] = (byte)((type << 4) | (Capability >> 8));
                        bytes[off + 3] = Capability & 0xff;
                        int m = bytes[off + 5] >> 5;
                        int hdr = bytes[off + 5] & 0x1f;
                        int crcAt = off + 2 + hdr - 2 + m;
                        ushort crc = Crc16S5066.Compute(bytes.AsSpan((off + 2)..crcAt));
                        Crc16S5066.Write(crc, bytes.AsSpan(crcAt, 2));
                        changed++;
                    }

                    if (DpduCodec.DecodeFirst(bytes.AsSpan(off), PeerEdition.Ed4) is not { IsSuccess: true } again || again.Consumed != r.Consumed)
                    {
                        throw new InvalidDataException("a patched D_PDU no longer decodes");
                    }

                    off += r.Consumed;
                }

                string before = (string)line["bytes"]!;
                output.Add(text.Replace(before, Convert.ToHexStringLower(bytes), StringComparison.Ordinal));
                continue;
            }

            output.Add(text);
        }

        return (output, changed);
    }

    private static int IndexOfSync(List<byte> b)
    {
        for (int i = 0; i + 1 < b.Count; i++)
        {
            if (b[i] == 0x90 && b[i + 1] == 0xeb)
            {
                return i;
            }
        }

        return -1;
    }

    private static uint ParseAddress(string s)
    {
        string[] p = s.Split('.');
        return (uint.Parse(p[0]) << 24) | (uint.Parse(p[1]) << 16) | (uint.Parse(p[2]) << 8) | uint.Parse(p[3]);
    }
}
