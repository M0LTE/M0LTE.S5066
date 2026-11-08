using System.Buffers.Binary;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Sweep;

/// <summary>
/// One client connection's application, speaking raw Annex S frames, so the same code runs over the
/// in-process link to a simulated node and over TCP to a real s5066d. Single-threaded: the caller
/// serialises <see cref="Receive"/> and <see cref="Poll"/>. Times are the caller's clock in ms.
/// </summary>
internal abstract class SisApp
{
    private readonly SisFrameDecoder _decoder = new();
    private readonly Queue<byte[]> _outbox = new();

    protected SisApp(byte sap) => Sap = sap;

    public byte Sap { get; }

    public ushort? Mtu { get; private set; }

    public bool FlowOn { get; private set; }

    public int Rejected { get; private set; }

    public string? BindFailure { get; private set; }

    /// <summary>The first frame: the bind (S_BIND_REQUEST, ARQ).</summary>
    public void Connected() => Send(new SisBindRequest(Sap, new ServiceType(1)));

    public bool TryTakeOutgoing(out byte[] bytes) => _outbox.TryDequeue(out bytes!);

    public void Receive(ReadOnlySpan<byte> bytes, long now)
    {
        _decoder.Push(bytes);
        while (_decoder.TryRead(out SisDecodeResult r))
        {
            if (r.Primitive is not { } p)
            {
                continue;
            }

            switch (p)
            {
                case SisBindAccepted a:
                    Mtu = a.Mtu;
                    FlowOn = true;   // flow is on after each bind (S-0020, interp S-0027)
                    break;
                case SisBindRejected j:
                    BindFailure = $"bind rejected, reason {j.Reason}";
                    break;
                case SisDataFlowOn:
                    FlowOn = true;
                    break;
                case SisDataFlowOff:
                    FlowOn = false;
                    break;
                case SisKeepAlive:
                    Send(new SisKeepAlive());   // answered at once (S-0041)
                    break;
                case SisUnidataRequestRejected:
                    Rejected++;
                    break;
                case SisUnidataIndication u:
                    OnIndication(u, now);
                    break;
            }
        }
    }

    /// <summary>Gives the app a chance to send. Returns true if it queued a frame.</summary>
    public virtual bool Poll(long now) => false;

    protected virtual void OnIndication(SisUnidataIndication u, long now)
    {
    }

    protected void Send(SisPrimitive p) => _outbox.Enqueue(SisCodec.Encode(p));

    /// <summary>A deterministic pseudo-random body for U_PDU number <paramref name="seq"/>, after an 8-byte prefix.</summary>
    public static void Fill(Span<byte> body, uint seq)
    {
        ulong s = Seeds.Mix("updu", seq);
        for (int i = 0; i < body.Length; i++)
        {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            body[i] = (byte)s;
        }
    }

    /// <summary>A U_PDU: sequence number, a time in ms, then the pseudo-random body.</summary>
    public static byte[] MakeUpdu(uint seq, long timeMs, int size)
    {
        var u = new byte[Math.Max(size, 8)];
        BinaryPrimitives.WriteUInt32BigEndian(u, seq);
        BinaryPrimitives.WriteUInt32BigEndian(u.AsSpan(4), (uint)timeMs);
        Fill(u.AsSpan(8), seq);
        return u;
    }

    /// <summary>Checks a received U_PDU against <see cref="MakeUpdu"/>; returns its sequence number and time.</summary>
    public static bool Check(ReadOnlySpan<byte> u, out uint seq, out long timeMs)
    {
        seq = 0;
        timeMs = 0;
        if (u.Length < 8)
        {
            return false;
        }

        seq = BinaryPrimitives.ReadUInt32BigEndian(u);
        timeMs = BinaryPrimitives.ReadUInt32BigEndian(u[4..]);
        Span<byte> expect = u.Length - 8 <= 4096 ? stackalloc byte[u.Length - 8] : new byte[u.Length - 8];
        Fill(expect, seq);
        return expect.SequenceEqual(u[8..]);
    }
}

/// <summary>The bulk sender: ARQ U_PDUs to one address and SAP, as fast as flow control allows (S-0019 to S-0021).</summary>
internal sealed class BulkSender(byte sap, SisAddress to, int size, byte priority) : SisApp(sap)
{
    private uint _seq;

    public long SentBytes { get; private set; }

    public long SentUpdus => _seq;

    public override bool Poll(long now)
    {
        if (Mtu is not ushort mtu || !FlowOn)
        {
            return false;
        }

        byte[] u = MakeUpdu(_seq++, now, Math.Min(size, mtu));
        Send(new SisUnidataRequest(priority, Sap, to, new ServiceType(1), 0, u));
        SentBytes += u.Length;
        return true;
    }
}

/// <summary>Chat-sized messages, one every <paramref name="intervalMs"/> from <paramref name="startMs"/>, ARQ.</summary>
internal sealed class ChatSender(byte sap, SisAddress to, int size, byte priority, long startMs, long intervalMs) : SisApp(sap)
{
    private uint _seq;
    private long _next = startMs;

    public long Sent => _seq;

    public override bool Poll(long now)
    {
        if (Mtu is not ushort mtu || intervalMs <= 0 || now < _next)
        {
            return false;
        }

        _next += intervalMs;
        if (!FlowOn)
        {
            return false;   // a chat line is not queued behind flow off; it is counted as not sent
        }

        byte[] u = MakeUpdu(_seq++, now, Math.Min(size, mtu));
        Send(new SisUnidataRequest(priority, Sap, to, new ServiceType(1), 0, u));
        return true;
    }
}

/// <summary>Receives and checks U_PDUs made by <see cref="SisApp.MakeUpdu"/>: bytes by time, duplicates, corruptions, latency.</summary>
internal sealed class Receiver(byte sap) : SisApp(sap)
{
    private readonly HashSet<uint> _seen = [];

    public List<(long Time, int Bytes)> Deliveries { get; } = [];

    public List<long> LatenciesMs { get; } = [];

    public int Duplicates { get; private set; }

    public int Corrupt { get; private set; }

    public long OutOfOrder { get; private set; }

    private long _lastSeq = -1;

    protected override void OnIndication(SisUnidataIndication u, long now)
    {
        if (!Check(u.UPdu.Span, out uint seq, out long sentAt))
        {
            Corrupt++;
            return;
        }

        if (!_seen.Add(seq))
        {
            Duplicates++;
            return;
        }

        if (seq < _lastSeq)
        {
            OutOfOrder++;
        }

        _lastSeq = Math.Max(_lastSeq, seq);
        Deliveries.Add((now, u.UPdu.Length));
        LatenciesMs.Add(now - sentAt);
    }
}
