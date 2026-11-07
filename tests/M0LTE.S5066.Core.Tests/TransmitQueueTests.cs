using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Core.Tests.T2;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The P3-4 transmit queue and planner on paths no phase 3 trace can reach: expedited (type 8)
/// requests come from the CAS-1 linking of P4-1, and ARQ requests are held by the SIS until then.
/// The end-to-end behaviour (EOT, budget, ordering, the MAC) is in the corpus scenarios.
/// </summary>
public class TransmitQueueTests
{
    private const uint X = 0x0103;
    private const uint Y = 0x0104;

    [Fact]
    public void Expedited_goes_first_with_its_own_id_space()
    {
        // C-0288 (expedited never behind normal data), C-0328 and C-0359 (separate ID spaces), C-1040.
        var q = new Rig();
        q.Tx.Add(Normal(X, 5, [1]));
        q.Tx.Add(Normal(X, 5, [2]));
        q.Tx.AddExpedited(Expedited(Y, [3]));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        pdus.Select(p => p.Type).Should().Equal(8, 7, 7);
        pdus.Select(Id).Should().Equal(0, 0, 1);
        q.Observer.Log.Last().Should().Be("QueueState:False");
    }

    [Fact]
    public void Normal_data_waits_behind_unplaced_expedited_data_to_the_same_destination()
    {
        // C-0615, C-0737, C-1044: the second expedited C_PDU to X does not fit, so the type 7 to X
        // waits; the type 7 to Y is not held. A misfit expedited item is skipped, not a stop.
        var q = new Rig(maxTransmissionMs: 720 + 4800);   // one 96-byte block
        q.Tx.AddExpedited(Expedited(X, new byte[10]));
        q.Tx.AddExpedited(Expedited(X, new byte[70]));
        q.Tx.Add(Normal(X, 15, [1]));
        q.Tx.Add(Normal(Y, 0, [2]));
        List<Dpdu> pdus = q.Decode(q.Tx.Build());
        pdus.Select(p => (p.Type, p.Header.Dst)).Should().Equal((8, X), (7, Y));
        q.Tx.Count.Should().Be(2);
        q.Observer.Log.Last().Should().Be("QueueState:True");

        // The 95-byte type 8 fills the next block on its own; the type 7 to X then goes last.
        q.Decode(q.Tx.Build()).Select(p => (p.Type, p.Header.Dst)).Should().Equal((8, X));
        q.Decode(q.Tx.Build()).Select(p => (p.Type, p.Header.Dst)).Should().Equal((7, X));
    }

    [Fact]
    public void Lower_priority_never_overtakes_a_data_item_that_does_not_fit()
    {
        // C-0851, C-0880: the 60-byte C_PDU does not fit after the first, so the small one waits too.
        var q = new Rig(maxTransmissionMs: 720 + 4800);
        q.Tx.Add(Normal(X, 9, new byte[20]));
        q.Tx.Add(Normal(X, 8, new byte[60]));
        q.Tx.Add(Normal(X, 1, [1]));
        q.Decode(q.Tx.Build()).Should().ContainSingle();
        q.Tx.Count.Should().Be(2);
    }

    [Fact]
    public void A_dpdu_longer_than_127_5_s_on_the_waveform_is_a_configuration_error()
    {
        var q = new Rig(blockBytes: 8, blockMs: 10000);
        q.Tx.Add(Normal(X, 0, new byte[200]));
        Action act = () => q.Tx.Build();
        act.Should().Throw<ConfigException>().Which.Setting.Should().Be("drc.default_speed");
    }

    [Fact]
    public void C_pdu_ids_wrap_modulo_4096()
    {
        // C-0326, C-0329.
        var q = new Rig();
        for (int i = 0; i < 4096; i++)
        {
            q.Tx.Add(Normal(X, 0, [1]));
            q.Tx.Build();
        }

        q.Tx.Add(Normal(X, 0, [1]));
        Id(q.Decode(q.Tx.Build()).Single()).Should().Be(0);
    }

    [Fact]
    public void Requests_needing_p3_5_are_queued_and_refused_only_at_build()
    {
        var q = new Rig();
        q.Tx.Add(Normal(X, 0, new byte[1024]));
        q.Tx.Count.Should().Be(1);
        FluentBuild(q).Should().Throw<NotSupportedException>();

        var r = new Rig();
        r.Tx.Add(Normal(X, 0, [1]) with { MinRetransmissions = 1 });
        FluentBuild(r).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Arq_requests_while_unconnected_are_rejected_to_the_cas()
    {
        // C-0573, C-0574, C-0731.
        var context = new CoreContext(TestConfig.Basic(), new NullModem());
        var log = new ObserverLog();
        var dts = new DtsSublayer(context, log);
        dts.On(Normal(X, 0, [1]) with { Arq = true, Handle = 7 });
        dts.On(new DExpeditedUnidataRequest(X, false, true, new byte[] { 1 }, 8));
        context.TryDequeue(out InternalPrimitive a).Should().BeTrue();
        a.Should().Be(new DUnidataRequestRejected(7, DReason.NoConnection));
        context.TryDequeue(out InternalPrimitive b).Should().BeTrue();
        b.Should().Be(new DExpeditedUnidataRequestRejected(8, DReason.NoConnection));
        log.Log.Should().BeEmpty();
    }

    [Fact]
    public void Unbind_and_disconnect_discard_held_arq_requests_of_that_sap_only()
    {
        // A-0260 (coordinator ruling).
        var context = new CoreContext(TestConfig.Basic(), new NullModem());
        var sis = new SisSublayer(context);
        var dispatcher = new Dispatcher(context, sis, new CasSublayer(context), new DtsSublayer(context, new ObserverLog()), new Probe("mac", []));
        foreach ((int conn, byte sap) in new[] { (1, (byte)5), (2, (byte)6) })
        {
            dispatcher.Handle(new SisConnect(0, conn));
            dispatcher.Handle(new SisBytes(0, conn, SisCodec.Encode(new SisBindRequest(sap, new ServiceType(1)))));
            dispatcher.Handle(new SisBytes(0, conn, SisCodec.Encode(new SisUnidataRequest(0, 1, new SisAddress(X), ServiceType.Default, 0, new byte[] { sap }))));
        }

        sis.AwaitingSoftLink.Should().HaveCount(2);
        dispatcher.Handle(new SisBytes(0, 1, SisCodec.Encode(new SisUnbindRequest())));
        sis.AwaitingSoftLink.Should().ContainSingle();
        dispatcher.Handle(new SisDisconnect(0, 2));
        sis.AwaitingSoftLink.Should().BeEmpty();
    }

    [Fact]
    public void Dts_and_drc_settings_are_read_and_checked()
    {
        JsonObject defaults = Corpus.T2Defaults.DeepClone().AsObject();
        defaults["local_address"] = "0.0.1.2";
        defaults["address_size"] = 4;
        NodeConfig c = ConfigReader.Read(defaults);
        c.Dts.Should().Be(new DtsConfig(1023, 127500));
        c.Drc.DefaultSpeed.Should().Be(new Speed(2, Interleaver.Long));

        foreach ((string key, string sub, int value) in new[] { ("dts", "max_segment_size", 1024), ("dts", "max_segment_size", 0), ("dts", "max_transmission_ms", 127501) })
        {
            JsonObject bad = defaults.DeepClone().AsObject();
            bad[key]![sub] = value;
            Action act = () => ConfigReader.Read(bad);
            act.Should().Throw<ConfigException>().Which.Setting.Should().Be($"{key}.{sub}");
        }

        JsonObject il = defaults.DeepClone().AsObject();
        il["drc"]!["default_speed"]!["interleaver"] = "longer";
        Action badIl = () => ConfigReader.Read(il);
        badIl.Should().Throw<ConfigException>();
    }

    private static Func<Transmit> FluentBuild(Rig q) => () => q.Tx.Build();

    private static int Id(Dpdu p) => p switch
    {
        NonArqData d => d.Fields.CPduId,
        ExpeditedNonArqData e => e.Fields.CPduId,
        _ => -1,
    };

    private static DUnidataRequest Normal(uint dst, int priority, byte[] cpdu) =>
        new(dst, false, false, priority, 1_000_000, false, 0, cpdu, 1);

    private static DExpeditedUnidataRequest Expedited(uint dst, byte[] cpdu) => new(dst, false, false, cpdu, 1);

    private sealed class Rig
    {
        public Rig(int maxTransmissionMs = 127500, int blockBytes = 96, int blockMs = 4800)
        {
            NodeConfig config = TestConfig.Basic(CsmaMode.Jitter) with
            {
                Modem = new ModemFixture(720, 0, [new ModemWaveform(2, Interleaver.Long, blockBytes, blockMs)]),
                Dts = new DtsConfig(1023, maxTransmissionMs),
            };
            var context = new CoreContext(config, new FixtureModem(config.Modem));
            Tx = new TransmitQueue(context, Observer);
        }

        public ObserverLog Observer { get; } = new();

        public TransmitQueue Tx { get; }

        public List<Dpdu> Decode(Transmit t)
        {
            var list = new List<Dpdu>();
            ReadOnlySpan<byte> rest = t.Bytes.Span;
            while (rest.Length > 0)
            {
                DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(rest, PeerEdition.Ed4);
                list.Add(r.Value!);
                rest = rest[r.Consumed..];
            }

            return list;
        }
    }

    private sealed class ObserverLog : IMacObserver
    {
        public List<string> Log { get; } = [];

        public void EotHeard(long predictedEndTime) => Log.Add("EotHeard");

        public void DpduHeard(uint src, uint dst, bool dstIsGroup) => Log.Add("DpduHeard");

        public void QueueState(bool nonEmpty) => Log.Add("QueueState:" + nonEmpty);
    }
}
