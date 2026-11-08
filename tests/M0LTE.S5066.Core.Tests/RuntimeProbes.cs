using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

internal sealed record ProbeToSis(string Tag) : ToSis;

internal sealed record ProbeToCas(string Tag) : ToCas;

internal sealed record ProbeToDts(string Tag) : ToDts;

/// <summary>
/// A stand-in for any of the four sublayers: it logs every call as "name.Method[:tag]" into a
/// shared log and runs a reaction registered for that call, so dispatcher tests can see the order
/// in which the runtime calls the sublayers and make them enqueue, emit or start timers.
/// </summary>
internal sealed class Probe(string name, List<string> log) : ISisSublayer, ICasSublayer, IDtsSublayer, IMacSublayer
{
    public Dictionary<string, Action> Reactions { get; } = new(StringComparer.Ordinal);

    public void OnConfigChanged(NodeConfig oldConfig, NodeConfig newConfig) => Call("OnConfigChanged");

    public void OnTimer(TimerName timer) => Call("OnTimer", timer.ToTraceString());

    public void OnConnect(int conn) => Call("OnConnect");

    public void OnBytes(int conn, ReadOnlyMemory<byte> bytes) => Call("OnBytes");

    public void OnDisconnect(int conn) => Call("OnDisconnect");

    public void OnShutdown() => Call("OnShutdown");

    public void OnReceiveStarted() => Call("OnReceiveStarted");

    public void OnReceiveEnded() => Call("OnReceiveEnded");

    public void On(ToSis primitive) => Call("On", primitive is ProbeToSis p ? p.Tag : primitive.GetType().Name);

    public void On(ToCas primitive) => Call("On", primitive is ProbeToCas p ? p.Tag : primitive.GetType().Name);

    public void On(ToDts primitive) => Call("On", primitive is ProbeToDts p ? p.Tag : primitive.GetType().Name);

    public void OnBurstStarted() => Call("OnBurstStarted");

    public void OnBurstStarted(int? blockMs) => Call("OnBurstStarted");

    public void OnBytesReceived(ReadOnlyMemory<byte> bytes) => Call("OnBytesReceived");

    public void OnBurstEnded(BurstEnded burst) => Call("OnBurstEnded");

    public void OnTransmitDone() => Call("OnTransmitDone");

    public void OnChannelBusy(bool busy) => Call("OnChannelBusy");

    public void EotHeard(long predictedEndTime) => Call("EotHeard");

    public void DpduHeard(uint src, uint dst, bool dstIsGroup) => Call("DpduHeard");

    public void QueueState(bool nonEmpty) => Call("QueueState");

    public void AfterInput() => Call("AfterInput");

    private void Call(string method, string? tag = null)
    {
        string key = tag is null ? method : method + ":" + tag;
        log.Add(name + "." + key);
        if (Reactions.TryGetValue(key, out Action? reaction))
        {
            reaction();
        }
    }
}

/// <summary>A context, four probes and a dispatcher wired together.</summary>
internal sealed class ProbeRig
{
    public ProbeRig()
    {
        Context = new CoreContext(TestConfig.Basic(), new NullModem());
        Sis = new Probe("sis", Log);
        Cas = new Probe("cas", Log);
        Dts = new Probe("dts", Log);
        Mac = new Probe("mac", Log);
        Dispatcher = new Dispatcher(Context, Sis, Cas, Dts, Mac);
    }

    public List<string> Log { get; } = [];

    public CoreContext Context { get; }

    public Probe Sis { get; }

    public Probe Cas { get; }

    public Probe Dts { get; }

    public Probe Mac { get; }

    public Dispatcher Dispatcher { get; }

    public List<CoreOutput> Outputs() => Context.TakeOutputs();
}

internal sealed class NullModem : IModemPort
{
    public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) =>
        throw new InvalidOperationException("no PlanBurst expected");
}

internal static class TestConfig
{
    public static NodeConfig Basic(CsmaMode? mode = null) => new(
        0x00000102,
        new CsmaConfig(mode, 3000, 16, 30000, 3000, null),
        new ModemFixture(720, 0, [new ModemWaveform(2, Interleaver.Long, 96, 4800)]))
    {
        Cas = new CasConfig(4, 60000, 2, 120000),
    };
}
