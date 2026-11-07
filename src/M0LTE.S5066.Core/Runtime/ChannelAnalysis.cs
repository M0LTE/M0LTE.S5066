using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>What the channel analysis found about another node's MAC mode (interp J-0082).</summary>
internal enum MacModeFinding
{
    /// <summary>The node is using WTRP: a WTRP D_PDU type or a WTRP token EOW was heard from it (J-0039, J-0070, J-0051).</summary>
    WtrpInUse = 1,

    /// <summary>A transmission from a node earlier reported using WTRP carried no WTRP evidence: it is now in P2P or CSMA mode (J-0046).</summary>
    CsmaInUse = 2,

    /// <summary>The node warned that a WTRP D_PDU type is unrecognised: it can run only P2P or CSMA, presumably a legacy node (J-0047, J-0048, J-0050).</summary>
    CsmaOnly = 3,

    /// <summary>The node warned of unexpected use of a WTRP D_PDU type: it runs a different MAC mode from the WARNING's addressee (J-0049, J-0050).</summary>
    OtherMode = 4,
}

/// <summary>
/// A channel analysis report for the operator (J-0051), from the analysis to the SIS, which passes it
/// to its clients as S_MANAGEMENT_MSG_INDICATION (S-0034, S-0037; interp J-0082). Our addition: a
/// subnetwork management notice, not a primitive of Tables B-1 or C-1, so it is routed to the SIS
/// directly.
/// </summary>
/// <param name="Node">The node the finding is about.</param>
/// <param name="Finding">What was found.</param>
/// <param name="Addressee">For <see cref="MacModeFinding.CsmaOnly"/> and <see cref="MacModeFinding.OtherMode"/>, the destination of the WARNING; otherwise 0.</param>
internal sealed record MacModeReport(uint Node, MacModeFinding Finding, uint Addressee) : ToSis;

/// <summary>
/// MAC-mode discovery, the channel analysis element [Ed4 J.4.1.2, p.J-7 to J-8] (J-0029, J-0031,
/// J-0043 to J-0050): what other nodes' transmissions say about the MAC mode they run, reported to
/// the operator (J-0051). It reads every D_PDU with a valid header, whoever it is addressed to
/// (interp J-0083), drives no state machine and produces nothing on the air; the WARNINGs J.4.1.1
/// asks for are the DTS state machine's (J-0033 to J-0039, J-0052 to J-0054, J-0070).
/// </summary>
/// <remarks>
/// <para>
/// Evidence, gathered per sender over one received burst, which is one transmission (J-0046):
/// an EXTENSION of extended type 1 to 4, the WTRP D_PDU types of Annex L (J-0023, J-0039; interp
/// J-0083), or a header carrying EOW type 13, the WTRP token (J-0070), says the sender is using WTRP.
/// A transmission from a sender with neither says it is in P2P or CSMA mode (J-0046). A WARNING
/// whose RECEIVED FRAME TYPE is 13 says, with reason 0 or 4, that its sender does not know the WTRP
/// types and can run only P2P or CSMA (J-0047, J-0048), and with reason 3 that its sender runs a
/// different MAC mode from the node it warned (J-0049), as J-0050 reads the two cases.
/// </para>
/// <para>
/// Judgement and reports, at the burst end, senders in address order (core-architecture.md 6 rule 3):
/// a sender with WTRP evidence is reported <see cref="MacModeFinding.WtrpInUse"/> unless that is
/// already the last mode reported for it; one without is reported
/// <see cref="MacModeFinding.CsmaInUse"/> only when the last mode reported for it was WTRP, since P2P
/// or CSMA is what every node is presumed to run. Then each WARNING finding of the burst, in byte
/// order, unless it repeats the last WARNING finding reported for that sender (interp J-0082). The
/// record of a node is created by its first report and removed when nothing reported about it
/// differs from the presumption (core-architecture.md 6 rule 11). A burst that never ends
/// is not judged: the next burst start clears it.
/// </para>
/// </remarks>
internal sealed class ChannelAnalysis
{
    /// <summary>The EOW type of the WTRP token (C-0411; Ed4 L, p.L-17).</summary>
    public const int TokenEowType = 13;

    private readonly CoreContext _context;

    /// <summary>Senders heard in the current burst, and whether each showed WTRP evidence.</summary>
    private readonly SortedDictionary<uint, bool> _burst = new();

    /// <summary>WARNING findings of the current burst, in byte order.</summary>
    private readonly List<MacModeReport> _warnings = [];

    /// <summary>What was last reported about each node, for nodes with something reported that differs from the presumption.</summary>
    private readonly SortedDictionary<uint, Reported> _reported = new();

    public ChannelAnalysis(CoreContext context) => _context = context;

    /// <summary>Whether a WTRP D_PDU type is an extended type Annex L assigns (Ed4 L, Table L-1, p.L-18; interp J-0083).</summary>
    public static bool IsWtrpType(int extendedType) => extendedType is >= 1 and <= 4;

    /// <summary>A new burst: the evidence of a burst that never ended is dropped.</summary>
    public void OnBurstStarted()
    {
        _burst.Clear();
        _warnings.Clear();
    }

    /// <summary>A valid header, whatever its destination (J-0043, interp J-0083): the sender is heard, and EOW 13 is WTRP evidence (J-0070).</summary>
    public void OnHeader(DpduHeaderInfo info)
    {
        DpduHeader h = info.Header;
        bool token = EowCodec.Decode(h.Eow).Type == TokenEowType;
        _burst[h.Src] = (_burst.TryGetValue(h.Src, out bool seen) && seen) || token;
    }

    /// <summary>A whole D_PDU, whatever its destination: a WTRP EXTENSION or a WARNING about type 13.</summary>
    public void OnDpdu(Dpdu pdu)
    {
        DpduHeader h = pdu.Header;
        switch (pdu)
        {
            case Extension x when IsWtrpType(x.ExtendedType):
                _burst[h.Src] = true;   // J-0039, J-0046
                break;
            case Warning { ReceivedFrameType: 13, Reason: 0 or 4 }:
                _warnings.Add(new MacModeReport(h.Src, MacModeFinding.CsmaOnly, h.Dst));   // J-0047, J-0048, J-0050
                break;
            case Warning { ReceivedFrameType: 13, Reason: 3 }:
                _warnings.Add(new MacModeReport(h.Src, MacModeFinding.OtherMode, h.Dst));   // J-0049, J-0050
                break;
        }
    }

    /// <summary>The burst ended: judge it and report what changed (J-0046 to J-0051; interp J-0082).</summary>
    public void OnBurstEnded()
    {
        foreach ((uint node, bool wtrp) in _burst)
        {
            bool wasWtrp = _reported.TryGetValue(node, out Reported? r) && r.Wtrp;
            if (wtrp == wasWtrp)
            {
                continue;
            }

            r ??= _reported[node] = new Reported();
            r.Wtrp = wtrp;
            Report(new MacModeReport(node, wtrp ? MacModeFinding.WtrpInUse : MacModeFinding.CsmaInUse, 0));
            Forget(node, r);
        }

        foreach (MacModeReport w in _warnings)
        {
            if (_reported.TryGetValue(w.Node, out Reported? r) && r.Warning == w)
            {
                continue;
            }

            r ??= _reported[w.Node] = new Reported();
            r.Warning = w;
            Report(w);
        }

        _burst.Clear();
        _warnings.Clear();
    }

    private void Report(MacModeReport report) => _context.Enqueue(report);

    private void Forget(uint node, Reported r)
    {
        if (!r.Wtrp && r.Warning is null)
        {
            _reported.Remove(node);
        }
    }

    private sealed class Reported
    {
        /// <summary>The last mode reported was WTRP.</summary>
        public bool Wtrp { get; set; }

        /// <summary>The last WARNING finding reported, or null.</summary>
        public MacModeReport? Warning { get; set; }
    }
}
