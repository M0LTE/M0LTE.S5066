using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// P5-3, Annex J MAC-mode discovery, on paths the mac corpus scenarios do not reach: the WARNING
/// element for the other MANAGEMENT subtypes (J-0036, J-0038, J-0054, J-0070, interp J-0081) and the
/// channel analysis reporting rules (J-0046 to J-0051, interp J-0082, J-0083).
/// </summary>
public class ChannelAnalysisTests
{
    private const uint Us = 0x0102;
    private const uint X = 0x0103;
    private const uint Y = 0x0109;

    [Fact]
    public void A_management_message_with_reserved_eow_14_draws_warning_reason_0_and_the_eow_answer()
    {
        // J-0036, J-0054, J-0081; C-0412, C-0445.
        var r = new PeersRig();
        r.Peers.OnDpdu(Msg(0xE12, valid: true, id: 3));
        r.Take().Should().Equal(new DWarningTransmitted(X, 6, 0));
        r.Tx.OwedEowFor(X).Should().Be(0x30E);
    }

    [Fact]
    public void A_management_acknowledgement_with_an_unknown_eow_draws_no_warning()
    {
        // J-0081: only a message is an extended management message; C-1079 still answers the EOW.
        var r = new PeersRig();
        r.Peers.OnDpdu(Msg(0x7AB, valid: false, id: 3));
        r.Take().Should().BeEmpty();
        r.Tx.OwedEowFor(X).Should().Be(0x307);
    }

    [Fact]
    public void A_management_message_with_the_token_eow_draws_warning_reason_3_and_no_eow_answer()
    {
        // J-0038, J-0070, J-0081.
        var r = new PeersRig();
        r.Peers.OnDpdu(Msg(0xD05, valid: true, id: 3));
        r.Take().Should().Equal(new DWarningTransmitted(X, 6, 3));
        r.Tx.OwedEowFor(X).Should().BeNull();
    }

    [Fact]
    public void The_token_eow_in_a_type_7_is_ignored_and_the_data_delivered()
    {
        // J-0070, J-0081; C-0585.
        var r = new PeersRig();
        var f = new NonArqFields(1, false, false, 2, 0, 10);
        r.Peers.OnDpdu(new NonArqData(new DpduHeader(0xD05, 10, 4, Us, X), f, new Octets([0, 5]), true));
        r.Take().Should().ContainSingle().Which.Should().BeOfType<DUnidataIndication>();
        r.Tx.OwedEowFor(X).Should().BeNull();
        r.Tx.PendingControl.Should().BeEmpty();
    }

    [Fact]
    public void A_repeated_management_message_with_an_unknown_subtype_is_not_warned_again()
    {
        // J-0081 with C-0316: the repeat comes after the first WARNING has gone out.
        var r = new PeersRig();
        r.Peers.OnDpdu(Msg(0x7AB, valid: true, id: 3));
        r.Take().Should().Equal(new DWarningTransmitted(X, 6, 0));
        r.Tx.Build();
        r.Peers.OnDpdu(Msg(0x7AB, valid: true, id: 3));
        r.Take().Should().BeEmpty();
        r.Tx.PendingControl.Should().ContainSingle().Which.Should().BeOfType<ManagementAck>();
    }

    [Fact]
    public void Extended_types_other_than_1_to_4_are_not_wtrp_evidence()
    {
        // Interp J-0083: Annex L assigns extended types 1 to 4 (Table L-1).
        var r = new AnalysisRig();
        r.Burst(Ext(X, Us, 5), Ext(X, Y, 0x42), Ext(X, Us, 0));
        r.Reports.Should().BeEmpty();
        ChannelAnalysis.IsWtrpType(1).Should().BeTrue();
        ChannelAnalysis.IsWtrpType(4).Should().BeTrue();
        ChannelAnalysis.IsWtrpType(5).Should().BeFalse();
    }

    [Fact]
    public void A_mode_change_is_reported_each_time_it_changes_and_not_otherwise()
    {
        // J-0046, J-0051, interp J-0082: WTRP, WTRP again (silent), P2P or CSMA, P2P or CSMA again (silent), WTRP.
        var r = new AnalysisRig();
        r.Burst(Ext(X, Us, 2));
        r.Burst(new Padding(new DpduHeader(0xD00, 1, 4, Y, X)));
        r.Burst(new Padding(new DpduHeader(0, 1, 4, Y, X)));
        r.Burst(new Padding(new DpduHeader(0, 1, 4, Y, X)));
        r.Burst(Ext(X, Y, 4));
        r.Reports.Should().Equal(
            new MacModeReport(X, MacModeFinding.WtrpInUse, 0),
            new MacModeReport(X, MacModeFinding.CsmaInUse, 0),
            new MacModeReport(X, MacModeFinding.WtrpInUse, 0));
    }

    [Fact]
    public void Senders_are_judged_in_address_order_before_warning_findings()
    {
        // Interp J-0082: mode findings by address, then WARNING findings in byte order.
        var r = new AnalysisRig();
        r.Burst(new Warning(new DpduHeader(0, 1, 4, Us, Y), 13, 0), Ext(Y, Us, 1), Ext(X, Us, 1));
        r.Reports.Should().Equal(
            new MacModeReport(X, MacModeFinding.WtrpInUse, 0),
            new MacModeReport(Y, MacModeFinding.WtrpInUse, 0),
            new MacModeReport(Y, MacModeFinding.CsmaOnly, Us));
    }

    [Fact]
    public void A_changed_warning_finding_is_reported_and_warnings_about_other_types_are_not()
    {
        // J-0047 to J-0050, interp J-0082: a different finding, or the same finding about a different addressee, is new.
        var r = new AnalysisRig();
        r.Burst(new Warning(new DpduHeader(0, 1, 4, Us, Y), 13, 4));
        r.Burst(new Warning(new DpduHeader(0, 1, 4, Us, Y), 13, 3));
        r.Burst(new Warning(new DpduHeader(0, 1, 4, X, Y), 13, 3));
        r.Burst(new Warning(new DpduHeader(0, 1, 4, X, Y), 13, 1), new Warning(new DpduHeader(0, 1, 4, X, Y), 7, 4));
        r.Reports.Should().Equal(
            new MacModeReport(Y, MacModeFinding.CsmaOnly, Us),
            new MacModeReport(Y, MacModeFinding.OtherMode, Us),
            new MacModeReport(Y, MacModeFinding.OtherMode, X));
    }

    [Fact]
    public void A_burst_that_never_ends_is_not_judged()
    {
        // Interp J-0083: the next burst start drops the evidence.
        var r = new AnalysisRig();
        r.Analysis.OnBurstStarted();
        r.Feed(Ext(X, Us, 1));
        r.Burst(new Padding(new DpduHeader(0, 1, 4, Y, X)));
        r.Reports.Should().BeEmpty();
    }

    private static Management Msg(int eow, bool valid, int id) =>
        new(new DpduHeader(eow, 10, 4, Us, X), false, valid, !valid, id, Octets.Empty);

    private static Extension Ext(uint src, uint dst, int type) => new(new DpduHeader(0, 10, 4, dst, src), type, Octets.Empty);

    private sealed class AnalysisRig
    {
        private readonly CoreContext _context;

        public AnalysisRig()
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter);
            _context = new CoreContext(config, new FixtureModem(config.Modem));
            Analysis = new ChannelAnalysis(_context);
        }

        public ChannelAnalysis Analysis { get; }

        public List<MacModeReport> Reports { get; } = [];

        public void Feed(Dpdu pdu)
        {
            Analysis.OnHeader(new DpduHeaderInfo(pdu.Type, pdu.Header, false));
            Analysis.OnDpdu(pdu);
        }

        public void Burst(params Dpdu[] pdus)
        {
            Analysis.OnBurstStarted();
            foreach (Dpdu p in pdus)
            {
                Feed(p);
            }

            Analysis.OnBurstEnded();
            while (_context.TryDequeue(out InternalPrimitive p))
            {
                Reports.Add((MacModeReport)p);
            }
        }
    }

    private sealed class PeersRig
    {
        private readonly CoreContext _context;

        public PeersRig()
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter);
            _context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(_context, new NoObserver());
            Peers = new DtsPeers(_context, Tx);
        }

        public TransmitQueue Tx { get; }

        public DtsPeers Peers { get; }

        public List<InternalPrimitive> Take()
        {
            var list = new List<InternalPrimitive>();
            while (_context.TryDequeue(out InternalPrimitive p))
            {
                list.Add(p);
            }

            return list;
        }
    }

    private sealed class NoObserver : IMacObserver
    {
        public void EotHeard(long predictedEndTime)
        {
        }

        public void DpduHeard(uint src, uint dst, bool dstIsGroup)
        {
        }

        public void QueueState(bool nonEmpty)
        {
        }
    }
}
