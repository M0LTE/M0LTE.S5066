namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The CAS (Annex B). P3-1 gives it the DATA C_PDU path in both directions (B-0297, B-0298):
/// an S_PDU from the SIS is wrapped in a DATA C_PDU and handed to the DTS for the peer with the
/// same delivery mode (B-0042, B-0112 to B-0116); a C_PDU from the DTS is checked, unwrapped and
/// delivered to the SIS. P4-1 adds the PeerTable and CAS-1 linking (core-architecture.md 4.2,
/// section 9).
/// </summary>
/// <remarks>
/// The request handle is passed through unchanged: the SIS took it from the dispatcher's single
/// counter, so it is already unique among DTS requests, and the CAS needs no handle map until it
/// issues requests of its own (P4-1).
/// </remarks>
internal sealed class CasSublayer : ICasSublayer
{
    private readonly CoreContext _context;

    public CasSublayer(CoreContext context) => _context = context;

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig)
    {
    }

    public void OnTimer(TimerName name) =>
        throw new InvalidOperationException($"CAS timer {name} expired, but the CAS starts no timers yet (now {_context.Now})");

    public void On(ToCas primitive)
    {
        switch (primitive)
        {
            case CUnidataRequest r:
                OnUnidataRequest(r);
                break;
            case DUnidataIndication d:
                OnUnidataIndication(d.Src, d.Dst, d.DstIsGroup, d.Arq, d.CPdu);
                break;
            case DExpeditedUnidataIndication d:
                // CAS-1 link C_PDUs travel here (B-0191); they are handled from P4-1 and discarded
                // until then. A DATA C_PDU is delivered as from the normal service (B-0298).
                OnUnidataIndication(d.Src, d.Dst, d.DstIsGroup, d.Arq, d.CPdu);
                break;
            case DWarningReceived or DWarningTransmitted:
                // Nothing in the CAS acts on a WARNING before CAS-1 linking (P4-1: Ed3 fallback).
                break;
            case DUnidataRequestRejected { Reason: DReason.TtlExpired } j:
                // A C_PDU the DTS dropped for TTL (C-0859, C-1050): the request fails upward with
                // the same reason (B-0018), handle unchanged.
                _context.Enqueue(new CUnidataRequestRejected(j.Handle, CReason.TtlExpired));
                break;
            default:
                throw new InvalidOperationException($"the CAS does not handle {primitive.GetType().Name} yet");
        }
    }

    /// <summary>
    /// Wraps the S_PDU in a DATA C_PDU, type 0 in the first nibble and then the S_PDU and nothing
    /// else (B-0112 to B-0114, B-0297), and passes it to the DTS's normal delivery service
    /// (B-0115) with the delivery mode the SIS gave it (B-0116).
    /// </summary>
    private void OnUnidataRequest(CUnidataRequest r)
    {
        byte[] cpdu = CpduCodec.Encode(new CpduData(r.SPdu));
        _context.Enqueue(new DUnidataRequest(r.Dst, r.DstIsGroup, r.Arq, r.Priority, r.Ttd, r.DeliverInOrder,
                                             r.MinRetransmissions, cpdu, r.Handle));
    }

    /// <summary>
    /// Checks a received C_PDU and, for a valid DATA C_PDU, removes the C_PCI and delivers the
    /// S_PDU to the SIS (B-0298). Valid means type 0 with at least one S_PDU octet; the low nibble
    /// of octet 0 is ignored; anything else is discarded (B-0325). The CAS-1 link C_PDUs (types 1
    /// to 6) are discarded here until P4-1 and P5-1 handle them.
    /// </summary>
    private void OnUnidataIndication(uint src, uint dst, bool dstIsGroup, bool arq, ReadOnlyMemory<byte> cpdu)
    {
        CodecResult<CPdu> decoded = CpduCodec.Decode(cpdu.Span);
        if (decoded.Value is CpduData data && !data.SPdu.IsEmpty)
        {
            _context.Enqueue(new CUnidataIndication(src, dst, dstIsGroup, arq, data.SPdu));
        }
    }
}
