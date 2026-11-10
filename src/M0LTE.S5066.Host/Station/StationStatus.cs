using System.Globalization;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host.Station;

/// <summary>
/// What the station status page shows (core-architecture.md 12.12): links, peers with the
/// waveform last used each way and the last SNR heard, the C_PDUs queued for each, the last
/// bursts, whether the node is transmitting and the PTT, and the CW ID schedule. It observes the
/// core's existing inputs and outputs on the host's loop and decodes D_PDU headers with its own
/// copy of the codec, as <see cref="HostTrace"/> does; it never changes what the node does.
/// </summary>
/// <remarks>
/// Fed on the host loop (<see cref="NodeHost"/>), the radio's transmit thread (<see cref="SetPtt"/>)
/// and read from the status server's threads, so every member takes one lock.
/// </remarks>
public sealed class StationStatus
{
    /// <summary>How many bursts the page lists, newest first.</summary>
    public const int BurstsKept = 20;

    private readonly object _gate = new();
    private readonly SortedDictionary<uint, (LinkStateName State, long T)> _links = new();
    private readonly SortedDictionary<uint, PeerInfo> _peers = new();
    private readonly LinkedList<BurstInfo> _bursts = new();
    private readonly List<byte> _rx = [];
    private DateTimeOffset _epoch;
    private uint? _address;
    private long _now;
    private bool _transmitting;
    private bool? _ptt;
    private CwIdSettings? _cwId;
    private CwIdSchedule? _cwSchedule;
    private BurstInfo? _sending;

    /// <summary>The host has started: its time 0 and the node's address.</summary>
    public void Started(DateTimeOffset epoch, uint localAddress)
    {
        lock (_gate)
        {
            _epoch = epoch;
            _address = localAddress;
        }
    }

    /// <summary>The CW ID the radio sends, with its schedule (on UTC Unix ms), or nothing for none.</summary>
    public void SetCwId(CwIdSettings? settings, CwIdSchedule? schedule)
    {
        lock (_gate)
        {
            _cwId = settings;
            _cwSchedule = schedule;
        }
    }

    /// <summary>The radio's PTT changed (true keyed). Never called on a node with no radio.</summary>
    public void SetPtt(bool keyed)
    {
        lock (_gate)
        {
            _ptt = keyed;
        }
    }

    /// <summary>An input the core is about to handle.</summary>
    public void Input(CoreInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            _now = input.T;
            switch (input)
            {
                case M0LTE.S5066.Core.Runtime.BurstStarted:
                    _rx.Clear();
                    break;
                case BytesReceived b:
                    _rx.AddRange(b.Bytes.Span);
                    break;
                case M0LTE.S5066.Core.Runtime.BurstEnded e:
                    string waveform = Waveform(e.Wn, e.Interleaver);
                    double? snr = e.SnrTenthDb is int s ? s / 10.0 : null;
                    var sources = new SortedSet<uint>();
                    foreach (DpduHeader h in Headers(_rx.ToArray()))
                    {
                        if (h.Src != _address)
                        {
                            sources.Add(h.Src);
                        }
                    }

                    foreach (uint src in sources)
                    {
                        PeerInfo p = Peer(src);
                        p.LastHeard = e.T;
                        p.RxWaveform = waveform;
                        p.RxSnrDb = snr;
                    }

                    Add(new BurstInfo("rx", e.T, waveform, _rx.Count, snr, [.. sources], e.Reason != BurstEndReason.Eom));
                    _rx.Clear();
                    break;
                case M0LTE.S5066.Core.Runtime.TransmitDone t:
                    _transmitting = false;
                    if (_sending is not null)
                    {
                        _sending.EndT = t.T;
                        _sending = null;
                    }

                    break;
            }
        }
    }

    /// <summary>An output of the input just handled, at core time <paramref name="t"/>.</summary>
    public void Output(CoreOutput output, long t)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            switch (output)
            {
                case Transmit x:
                    string waveform = Waveform(x.Wn, x.Interleaver);
                    var peers = new SortedSet<uint>();
                    foreach (DpduHeader h in Headers(x.Bytes.ToArray()))
                    {
                        peers.Add(h.Dst);
                    }

                    foreach (uint dst in peers)
                    {
                        PeerInfo p = Peer(dst);
                        p.LastSent = t;
                        p.TxWaveform = waveform;
                    }

                    _transmitting = true;
                    _sending = new BurstInfo("tx", t, waveform, x.Bytes.Length, null, [.. peers], false);
                    Add(_sending);
                    break;
                case LinkState l:
                    _links[l.Peer] = (l.State, t);
                    Peer(l.Peer);
                    break;
            }
        }
    }

    /// <summary>The C_PDUs the core holds per destination, sampled after each input.</summary>
    public void Queues(IReadOnlyList<(uint Peer, int Arq, int NonArq)> depths)
    {
        ArgumentNullException.ThrowIfNull(depths);
        lock (_gate)
        {
            foreach (PeerInfo p in _peers.Values)
            {
                p.QueuedArq = 0;
                p.QueuedNonArq = 0;
            }

            foreach ((uint peer, int arq, int nonArq) in depths)
            {
                PeerInfo p = Peer(peer);
                p.QueuedArq = arq;
                p.QueuedNonArq = nonArq;
            }
        }
    }

    /// <summary>The status as JSON (the page's <c>/status.json</c>).</summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var links = new JsonArray();
            foreach ((uint peer, (LinkStateName state, long t)) in _links)
            {
                links.Add(new JsonObject { ["peer"] = Dotted(peer), ["state"] = state.ToString().ToLowerInvariant(), ["since"] = Utc(t) });
            }

            var peers = new JsonArray();
            foreach ((uint address, PeerInfo p) in _peers)
            {
                peers.Add(new JsonObject
                {
                    ["peer"] = Dotted(address),
                    ["link"] = _links.TryGetValue(address, out (LinkStateName State, long T) l) ? l.State.ToString().ToLowerInvariant() : "unlinked",
                    ["tx_waveform"] = p.TxWaveform,
                    ["last_sent"] = Utc(p.LastSent),
                    ["rx_waveform"] = p.RxWaveform,
                    ["rx_snr_db"] = p.RxSnrDb,
                    ["last_heard"] = Utc(p.LastHeard),
                    ["queued_arq"] = p.QueuedArq,
                    ["queued_non_arq"] = p.QueuedNonArq,
                });
            }

            var bursts = new JsonArray();
            foreach (BurstInfo b in _bursts)
            {
                bursts.Add(new JsonObject
                {
                    ["dir"] = b.Dir,
                    ["at"] = Utc(b.T),
                    ["waveform"] = b.Waveform,
                    ["bytes"] = b.Bytes,
                    ["snr_db"] = b.SnrDb,
                    ["peers"] = new JsonArray([.. b.Peers.Select(p => (JsonNode?)JsonValue.Create(Dotted(p)))]),
                    ["cut_short"] = b.CutShort,
                    ["ms"] = b.EndT is long end ? end - b.T : null,
                });
            }

            JsonObject? cw = null;
            if (_cwId is { } c)
            {
                CwIdState? s = _cwSchedule?.State();
                cw = new JsonObject
                {
                    ["text"] = c.Text,
                    ["wpm"] = c.Wpm,
                    ["tone_hz"] = c.ToneHz,
                    ["seconds"] = Math.Round(c.DurationMs / 1000.0, 1),
                    ["at_start"] = c.AtStart,
                    ["every_minutes"] = c.EveryMinutes,
                    ["sent"] = s?.Sent ?? 0,
                    ["last_id"] = UnixUtc(s?.LastIdMs),
                    ["periodic_due_from"] = UnixUtc(s?.PeriodicDueFromMs),
                    ["start_id_due_from"] = c.AtStart ? (s?.LastKeyupMs is long k ? UnixUtc(k + CwIdSchedule.QuietMs) : "next transmission") : null,
                };
            }

            return new JsonObject
            {
                ["node"] = _address is uint a ? Dotted(a) : null,
                ["now"] = Utc(_now),
                ["transmitting"] = _transmitting,
                ["ptt"] = _ptt is bool k2 ? (k2 ? "keyed" : "unkeyed") : null,
                ["links"] = links,
                ["peers"] = peers,
                ["queued_arq"] = _peers.Values.Sum(p => p.QueuedArq),
                ["queued_non_arq"] = _peers.Values.Sum(p => p.QueuedNonArq),
                ["bursts"] = bursts,
                ["cw_id"] = cw,
            };
        }
    }

    /// <summary>A waveform as the page and the radio's log lines show it, for example <c>wn6 short</c>.</summary>
    internal static string Waveform(int wn, Interleaver il) => $"wn{wn} {InterleaverNames.ToName(il).Replace('_', ' ')}";

    /// <summary>The header of each D_PDU in a byte stream, resyncing a byte at a time past anything that does not decode.</summary>
    internal static List<DpduHeader> Headers(byte[] bytes)
    {
        var headers = new List<DpduHeader>();
        int pos = 0;
        while (pos < bytes.Length)
        {
            DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(bytes.AsSpan(pos), PeerEdition.Any);
            if (r.IsSuccess)
            {
                headers.Add(r.Value!.Header);
                pos += r.Consumed;
            }
            else if (r.Error == PduErrorKind.Truncated)
            {
                break;
            }
            else
            {
                pos++;
            }
        }

        return headers;
    }

    private PeerInfo Peer(uint address)
    {
        if (!_peers.TryGetValue(address, out PeerInfo? p))
        {
            p = new PeerInfo();
            _peers[address] = p;
        }

        return p;
    }

    private void Add(BurstInfo b)
    {
        _bursts.AddFirst(b);
        while (_bursts.Count > BurstsKept)
        {
            _bursts.RemoveLast();
        }
    }

    private static string Dotted(uint address) => DpduAddress.ToDotted(address);

    private string? Utc(long? t) => t is long ms ? (_epoch + TimeSpan.FromMilliseconds(ms)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : null;

    private static string? UnixUtc(long? ms) => ms is long v ? DateTimeOffset.FromUnixTimeMilliseconds(v).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) : null;

    private sealed class PeerInfo
    {
        public string? TxWaveform { get; set; }

        public long? LastSent { get; set; }

        public string? RxWaveform { get; set; }

        public double? RxSnrDb { get; set; }

        public long? LastHeard { get; set; }

        public int QueuedArq { get; set; }

        public int QueuedNonArq { get; set; }
    }

    private sealed class BurstInfo(string dir, long t, string waveform, int bytes, double? snrDb, uint[] peers, bool cutShort)
    {
        public string Dir { get; } = dir;

        public long T { get; } = t;

        public string Waveform { get; } = waveform;

        public int Bytes { get; } = bytes;

        public double? SnrDb { get; } = snrDb;

        public uint[] Peers { get; } = peers;

        public bool CutShort { get; } = cutShort;

        public long? EndT { get; set; }
    }
}
