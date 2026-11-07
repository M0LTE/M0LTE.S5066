using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The parts of the P3-1 SIS and CAS that no T2 trace can see yet: what goes into the DTS queue
/// (seen on air from P3-4), the receive path (seen from P3-6), held ARQ requests (P4-1) and the TTD
/// arithmetic. The rig wires the real SIS, CAS and DTS with a probe MAC.
/// </summary>
public class SisCasTests
{
    private static readonly SisAddress Peer = new(0x0103);

    private static byte[] Bind(byte sap, ServiceType st) => SisCodec.Encode(new SisBindRequest(sap, st));

    private static byte[] Unidata(byte prio, byte sap, SisAddress dst, ServiceType mode, uint ttl, byte[] u) =>
        SisCodec.Encode(new SisUnidataRequest(prio, sap, dst, mode, ttl, u));

    [Fact]
    public void Non_arq_request_is_wrapped_in_data_cpdu_and_queued_with_its_attributes()
    {
        var rig = new Rig();
        rig.Connect(1, Bind(5, new ServiceType(2, Order: 1, MinRetx: 3)));
        rig.Send(1000, 1, Unidata(9, 7, Peer, ServiceType.Default, 30, [0xAA, 0xBB]));

        DUnidataRequest r = rig.Dts.PendingNonArq.Should().ContainSingle().Subject;
        r.Dst.Should().Be(0x0103u);
        r.DstIsGroup.Should().BeFalse();
        r.Arq.Should().BeFalse();
        r.Priority.Should().Be(9);
        r.Ttd.Should().Be(1000 + (30 * 2000));
        r.DeliverInOrder.Should().BeFalse();
        r.MinRetransmissions.Should().Be(3);

        // DATA C_PDU: type 0 octet, then the DATA S_PDU (B-0113, B-0114).
        CpduData c = CpduCodec.Decode(r.CPdu.Span).Value.Should().BeOfType<CpduData>().Subject;
        r.CPdu.Span[0].Should().Be(0x00);
        SpduData s = SpduCodec.Decode(c.SPdu.Span).Value.Should().BeOfType<SpduData>().Subject;
        s.Should().Be(new SpduData(9, 5, 7, false, new SpduTtd(1, 30), new byte[] { 0xAA, 0xBB }));   // day 1, 61 s -> 30 (A-0163)
        rig.MacLog.Should().Equal("QueueState:True");
        rig.Outputs.Should().BeEmpty();
    }

    [Fact]
    public void Ttl_zero_uses_the_default_and_a_large_field_is_capped()
    {
        var rig = new Rig(c => c with { Sis = new SisConfig(100) });
        rig.Connect(1, Bind(5, new ServiceType(2)));
        rig.Send(0, 1, Unidata(0, 1, Peer, ServiceType.Default, 0, []));
        rig.Send(0, 1, Unidata(0, 1, Peer, ServiceType.Default, 1048575, []));
        rig.Dts.PendingNonArq.Select(r => r.Ttd).Should().Equal(200_000L, 648000L * 2000);
    }

    [Fact]
    public void Group_destination_keeps_its_flag_and_override_sets_retransmissions()
    {
        var rig = new Rig();
        rig.Connect(1, Bind(5, new ServiceType(1)));
        rig.Send(0, 1, Unidata(3, 2, new SisAddress(0x09, Group: true), new ServiceType(2, Order: 0, MinRetx: 15), 10, [1]));
        DUnidataRequest r = rig.Dts.PendingNonArq.Should().ContainSingle().Subject;
        r.DstIsGroup.Should().BeTrue();
        r.MinRetransmissions.Should().Be(15);
        r.DeliverInOrder.Should().BeFalse();   // in-order asked with non-ARQ is sent in arrival order (S-0150)
    }

    [Fact]
    public void Arq_request_is_held_for_the_soft_link_with_client_confirm_flag()
    {
        var rig = new Rig();
        rig.Connect(1, Bind(5, new ServiceType(1, Confirm: 2, Order: 0)));
        rig.Send(0, 1, Unidata(4, 6, Peer, ServiceType.Default, 10, [7]));
        rig.Dts.PendingNonArq.Should().BeEmpty();
        rig.MacLog.Should().BeEmpty();
        CUnidataRequest held = rig.Sis.AwaitingSoftLink.Should().ContainSingle().Subject;
        held.Arq.Should().BeTrue();
        held.DeliverInOrder.Should().BeTrue();
        held.MinRetransmissions.Should().Be(0);
        SpduData s = SpduCodec.Decode(held.SPdu.Span).Value.Should().BeOfType<SpduData>().Subject;
        s.ClientConfirm.Should().BeTrue();     // A-0154
    }

    [Fact]
    public void Received_data_cpdu_is_delivered_to_the_bound_client()
    {
        var rig = new Rig();
        rig.Connect(1, Bind(7, new ServiceType(2)));
        byte[] spdu = SpduCodec.Encode(new SpduData(11, 3, 7, false, new SpduTtd(2, 100), new byte[] { 0x42 }));
        rig.Deliver(new DUnidataIndication(0x0103, 0x09, true, false, CpduCodec.Encode(new CpduData(spdu))));
        var expected = new SisUnidataIndication(11, 7, new SisAddress(0x09, Group: true), 2, 3, new SisAddress(0x0103), new byte[] { 0x42 });
        SisBytesOut o = rig.Outputs.Should().ContainSingle().Which.Should().BeOfType<SisBytesOut>().Subject;
        o.Conn.Should().Be(1);
        o.Bytes.ToArray().Should().Equal(SisCodec.Encode(expected));
    }

    [Fact]
    public void Received_data_cpdu_over_the_mtu_is_delivered()
    {
        var rig = new Rig();
        rig.Connect(1, Bind(7, new ServiceType(2)));
        byte[] big = Enumerable.Range(0, SisSublayer.Mtu + 952).Select(i => (byte)i).ToArray();
        byte[] spdu = SpduCodec.Encode(new SpduData(0, 3, 7, false, null, big));
        rig.Deliver(new DUnidataIndication(0x0103, 0x0102, false, false, CpduCodec.Encode(new CpduData(spdu))));
        SisDecodeResult r = SisCodec.DecodeFrame(((SisBytesOut)rig.Outputs.Single()).Bytes.Span);
        ((SisUnidataIndication)r.Primitive!).UPdu.ToArray().Should().Equal(big);   // the MTU limits only client requests (A-0046)
    }

    [Fact]
    public void Arq_reception_reports_transmission_mode_1()
    {
        var rig = new Rig();
        rig.Connect(1, Bind(7, new ServiceType(1)));
        byte[] spdu = SpduCodec.Encode(new SpduData(0, 3, 7, false, null, new byte[] { 1 }));
        rig.Deliver(new DUnidataIndication(0x0103, 0x0102, false, true, CpduCodec.Encode(new CpduData(spdu))));
        SisDecodeResult r = SisCodec.DecodeFrame(((SisBytesOut)rig.Outputs.Single()).Bytes.Span);
        ((SisUnidataIndication)r.Primitive!).TxMode.Should().Be(1);
    }

    [Theory]
    [InlineData("00")]          // DATA C_PDU with no S_PDU octet (B-0325)
    [InlineData("10")]          // PHYSICAL LINK REQUEST: not data
    [InlineData("f0")]          // undefined C_PDU type
    [InlineData("0030")]        // DATA C_PDU holding a reserved S_PDU type
    [InlineData("000075")]      // DATA S_PDU for SAP 5, which nobody holds (A-0235)
    public void Invalid_or_undeliverable_receptions_are_discarded(string cpdu)
    {
        var rig = new Rig();
        rig.Connect(1, Bind(7, new ServiceType(2)));
        rig.Deliver(new DUnidataIndication(0x0103, 0x0102, false, false, Convert.FromHexString(cpdu)));
        rig.Outputs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2026, 1, 0, 0L, 1, 0)]
    [InlineData(2026, 1, 0, 1999L, 1, 0)]                    // rounded down to even seconds (A-0163, A-0251)
    [InlineData(2026, 1, 86399, 1000L, 2, 0)]                // next day
    [InlineData(2026, 365, 86398, 2000L, 1, 0)]              // 2026 is not a leap year: day 365 wraps to day 1
    [InlineData(2028, 365, 0, 86_400_000L, 366 % 16, 0)]     // 2028 is: day 366 exists
    [InlineData(2100, 365, 0, 86_400_000L, 1, 0)]            // 2100 is not
    [InlineData(2000, 1, 0, 366L * 86_400_000, 1, 0)]        // 2000 is (400 rule): 366 days on is 2001 day 1
    [InlineData(2026, 100, 3600, 1_296_000_000L, 115 % 16, 1800)]   // 15 days on
    public void Ttd_is_day_of_year_mod_16_and_half_seconds(int year, int day, int second, long ms, int dayMod16, int half) =>
        TtdCalendar.Encode(new TimeConfig(year, day, second), ms).Should().Be(new SpduTtd((byte)dayMod16, (ushort)half));

    [Fact]
    public void Reads_the_p3_1_settings()
    {
        NodeConfig c = ConfigReader.Read(ConfigMerge.Merge(Corpus.T2Defaults, new JsonObject
        {
            ["local_address"] = "0.0.1.2", ["address_size"] = 4, ["groups"] = new JsonArray("0.0.0.9", "15.255.255.255"),
            ["time"] = new JsonObject { ["epoch_year"] = 2028, ["epoch_day"] = 366, ["epoch_second"] = 86399 },
            ["sis"] = new JsonObject { ["default_ttl"] = 5 },
        }));
        c.Groups.Should().Equal(0x09u, 0x0FFFFFFFu);
        c.Time.Should().Be(new TimeConfig(2028, 366, 86399));
        c.Sis.Should().Be(new SisConfig(5));

        NodeConfig d = ConfigReader.Read(ConfigMerge.Merge(Corpus.T2Defaults, new JsonObject { ["local_address"] = "0.0.1.2", ["address_size"] = 4 }));
        d.Groups.Should().BeEmpty();
        d.Time.Should().Be(new TimeConfig(2026, 1, 0));
        d.Sis.Should().Be(new SisConfig(648000));
    }

    [Theory]
    [InlineData("groups", "[\"16.0.0.0\"]")]
    [InlineData("time", "{\"epoch_year\": 2026, \"epoch_day\": 366, \"epoch_second\": 0}")]
    [InlineData("time", "{\"epoch_year\": 2026, \"epoch_day\": 0, \"epoch_second\": 0}")]
    [InlineData("time", "{\"epoch_year\": 2026, \"epoch_day\": 1, \"epoch_second\": 86400}")]
    [InlineData("sis", "{\"default_ttl\": 0}")]
    [InlineData("sis", "{\"default_ttl\": 648001}")]
    public void Bad_p3_1_settings_are_rejected(string key, string json)
    {
        var o = new JsonObject { ["local_address"] = "0.0.1.2", ["address_size"] = 4, [key] = JsonNode.Parse(json) };
        FluentActions.Invoking(() => ConfigReader.Read(ConfigMerge.Merge(Corpus.T2Defaults, o)))
            .Should().Throw<ConfigException>().Which.Setting.Should().StartWith(key);
    }

    /// <summary>The real SIS, CAS and DTS over a context, with a probe MAC that logs what the DTS tells it.</summary>
    private sealed class Rig
    {
        private readonly CoreContext _context;
        private readonly Dispatcher _dispatcher;

        public Rig(Func<NodeConfig, NodeConfig>? configure = null)
        {
            NodeConfig config = TestConfig.Basic();
            _context = new CoreContext(configure is null ? config : configure(config), new NullModem());
            var mac = new Probe("mac", []);
            Sis = new SisSublayer(_context);
            Dts = new DtsSublayer(_context, new QueueStateLogger(MacLog));
            _dispatcher = new Dispatcher(_context, Sis, new CasSublayer(_context), Dts, mac);
        }

        public SisSublayer Sis { get; }

        public DtsSublayer Dts { get; }

        public List<string> MacLog { get; } = [];

        public List<CoreOutput> Outputs { get; } = [];

        public void Connect(int conn, byte[] bind)
        {
            _dispatcher.Handle(new SisConnect(_context.Now, conn));
            _dispatcher.Handle(new SisBytes(_context.Now, conn, bind));
            _context.TakeOutputs().Should().ContainSingle().Which.Should().BeOfType<SisBytesOut>()
                .Which.Bytes.Span[5].Should().Be((byte)SisPrimitiveType.BindAccepted);
        }

        public void Send(long t, int conn, byte[] bytes)
        {
            _dispatcher.Handle(new SisBytes(t, conn, bytes));
            Outputs.AddRange(_context.TakeOutputs());
        }

        /// <summary>Puts a DTS-to-CAS primitive on the FIFO and drains it, as the DTS receiver will (P3-6).</summary>
        public void Deliver(ToCas primitive)
        {
            _context.Enqueue(primitive);
            _dispatcher.Handle(new ChannelBusy(_context.Now, false));
            Outputs.AddRange(_context.TakeOutputs());
        }
    }

    private sealed class QueueStateLogger(List<string> log) : IMacObserver
    {
        public void EotHeard(long predictedEndTime) => log.Add("EotHeard");

        public void DpduHeard(uint src, uint dst, bool dstIsGroup) => log.Add("DpduHeard");

        public void QueueState(bool nonEmpty) => log.Add("QueueState:" + nonEmpty);
    }
}
