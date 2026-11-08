using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// Flow control, node information and shutdown in the SIS (P6-2) on paths the sis corpus scenarios
/// do not reach: ARQ U_PDUs leaving the count on the CAS confirm, a client that goes away while held
/// off, the soft link states and limits of the node information record, and shutdown. The rig is
/// <see cref="CasLinkTests.Rig"/>: real SIS, CAS and DTS.
/// </summary>
public class SisFlowManagementTests
{
    private const uint X = 0x0103;
    private const uint Us = 0x0102;

    private static CasLinkTests.Rig Rig() => new();

    private static byte[] Primitive(SisPrimitive p) => SisCodec.Encode(p);

    private static List<byte[]> SisOut(CasLinkTests.Rig rig) => rig.Outputs.OfType<SisBytesOut>().Select(b => b.Bytes.ToArray()).ToList();

    private static SisUnidataRequest Big(uint dst) => new(0, 9, new SisAddress(dst), ServiceType.Default, 0, new byte[SisSublayer.Mtu]);

    /// <summary>Binds conn 1 for ARQ, sends eight MTU-sized U_PDUs to X and has X accept the call; returns their handles.</summary>
    private static List<int> HeldOffByArq(CasLinkTests.Rig rig)
    {
        rig.ArqRequest(X);   // binds, and one 1-byte U_PDU
        for (int i = 0; i < 7; i++)
        {
            rig.Client(Big(X));
        }

        SisOut(rig).Should().BeEmpty("one byte short of eight MTUs");
        rig.Client(Big(X));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Primitive(new SisDataFlowOff()));
        rig.Sent(X);
        rig.Deliver(new DExpeditedUnidataIndication(X, Us, false, false, CpduCodec.Encode(new PhysicalLinkAccepted(false, true, false))));
        return rig.Sis.Outstanding.Select(r => r.Handle).ToList();
    }

    [Fact]
    public void Arq_u_pdus_count_until_the_cas_confirms_them()
    {
        // Interp S-0163: FLOW_OFF at eight MTUs (8 x 2048 + 1 here), FLOW_ON once at or below four.
        var rig = Rig();
        List<int> handles = HeldOffByArq(rig);
        handles.Should().HaveCount(9);
        foreach (int h in handles.Take(4))
        {
            rig.Deliver(new DUnidataRequestConfirm(h));
            SisOut(rig).Should().BeEmpty("still above four MTUs");
        }

        rig.Deliver(new DUnidataRequestConfirm(handles[4]));
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Primitive(new SisDataFlowOn()));
        rig.Deliver(new DUnidataRequestConfirm(handles[5]));
        SisOut(rig).Should().BeEmpty("flow is already on");
    }

    [Fact]
    public void A_client_that_goes_away_while_held_off_leaves_nothing_behind()
    {
        // Interp S-0163: the count goes with the binding; later confirms are for nobody.
        var rig = Rig();
        List<int> handles = HeldOffByArq(rig);
        rig.Disconnect();
        foreach (int h in handles)
        {
            rig.Deliver(new DUnidataRequestConfirm(h));
        }

        SisOut(rig).Should().BeEmpty();
    }

    [Fact]
    public void Node_information_shows_an_established_soft_link_and_answers_a_bound_client()
    {
        // Interp S-0164: soft link state 2 when established; the asking client gets the answer
        // whether or not it is bound.
        var rig = Rig();
        rig.ArqRequest(X);
        rig.Sent(X);
        rig.Deliver(new DExpeditedUnidataIndication(X, Us, false, false, CpduCodec.Encode(new PhysicalLinkAccepted(false, true, false))));
        rig.Client(new SisManagementMsgRequest(SisSublayer.NodeInfoMsgType, new byte[] { 7 }));
        byte[] body = Convert.FromHexString("e0000102" + "0204" + "01" + "e0000103" + "02" + "0204" + "ffff" + "8000" + "ffff");
        SisOut(rig).Should().ContainSingle().Which.Should().Equal(Primitive(new SisManagementMsgIndication(2, body)));
    }

    [Fact]
    public void Node_information_lists_at_most_255_peers_and_clamps_its_fields()
    {
        // Interp S-0164: the count is one byte; SNR is clamped to the signed 16-bit range and the
        // age saturates at 65534 s.
        var rig = Rig();
        var quality = new SortedDictionary<uint, RxQuality>();
        for (uint a = 1; a <= 300; a++)
        {
            quality[a] = new RxQuality(0, a == 1 ? -400000 : a == 2 ? 400000 : null, 2, Interleaver.Long, 7, 1, 0);
        }

        rig.Context.RxQuality = quality;
        rig.Context.Now = 100_000_000;
        rig.ArqRequest(0x0FFF_FFFF);   // binds; the soft link record of the last address is beyond the cap
        rig.Client(new SisManagementMsgRequest(SisSublayer.NodeInfoMsgType, ReadOnlyMemory<byte>.Empty));
        byte[] frame = SisOut(rig).Single();
        frame[5..7].Should().Equal(19, SisSublayer.NodeInfoMsgType);
        ReadOnlySpan<byte> body = frame.AsSpan(7);   // after the 5-byte frame header, the type and the MSG TYPE
        body[6].Should().Be(255);
        body.Length.Should().Be(7 + (255 * 13));
        body.Slice(7, 13).ToArray().Should().Equal(Convert.FromHexString("e0000001" + "00" + "0204" + "0204" + "8001" + "fffe"));
        body.Slice(20, 13).ToArray().Should().Equal(Convert.FromHexString("e0000002" + "00" + "0204" + "0204" + "7fff" + "fffe"));
        body.Slice(33 + 9, 2).ToArray().Should().Equal(0x80, 0x00);   // no SNR given
    }

    [Fact]
    public void Shutdown_sends_unbind_reason_6_to_bound_clients_and_closes_every_connection()
    {
        // Interp A-0059, in connection order; unbound connections get no indication.
        var node = new Node(TestConfig.Basic(), 0, new NullModem());
        foreach (int conn in new[] { 1, 2, 3 })
        {
            node.Handle(new SisConnect(0, conn));
        }

        node.Handle(new SisBytes(0, 1, Primitive(new SisBindRequest(5, new ServiceType(2)))));
        node.Handle(new SisBytes(0, 3, Primitive(new SisBindRequest(6, new ServiceType(2)))));
        node.DrainOutputs();
        node.Shutdown(1000);
        byte[] unbind = Primitive(new SisUnbindIndication(6));
        List<CoreOutput> outputs = node.DrainOutputs();
        outputs.Should().HaveCount(5);
        outputs[0].Should().BeOfType<SisBytesOut>().Which.Conn.Should().Be(1);
        ((SisBytesOut)outputs[0]).Bytes.ToArray().Should().Equal(unbind);
        outputs[1].Should().Be(new SisClose(1));
        outputs[2].Should().Be(new SisClose(2));
        outputs[3].Should().BeOfType<SisBytesOut>().Which.Conn.Should().Be(3);
        ((SisBytesOut)outputs[3]).Bytes.ToArray().Should().Equal(unbind);
        outputs[4].Should().Be(new SisClose(3));
        node.Handle(new SisBytes(2000, 1, Primitive(new SisKeepAlive())));
        node.DrainOutputs().Should().BeEmpty("the connection is closed");
    }
}
