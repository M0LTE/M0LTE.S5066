using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Ip;

/// <summary>A configuration the client refuses; the message names the key.</summary>
internal sealed class IpConfigException(string message) : Exception(message);

/// <summary>ARQ or non-ARQ delivery for a datagram (U-0013, U-0016).</summary>
internal enum DeliveryKind
{
    /// <summary>ARQ with node delivery confirmation (U-0018).</summary>
    Arq,

    /// <summary>Non-ARQ, no confirmation, as they arrive (U-0021).</summary>
    NonArq,
}

/// <summary>What a rule or allow entry matches; a field that is null matches anything.</summary>
/// <param name="Prefix">Destination prefix.</param>
/// <param name="Protocol">IP protocol number (1 ICMP, 6 TCP, 17 UDP, 58 ICMPv6).</param>
/// <param name="Port">A TCP or UDP port, matched against the source and the destination port.</param>
internal sealed record PacketMatch(IPNetwork? Prefix, int? Protocol, int? Port)
{
    /// <summary>True when the datagram matches every field given.</summary>
    public bool Matches(IpDatagram d) =>
        (Prefix is not { } p || p.Contains(d.Destination))
        && (Protocol is not int proto || proto == d.Protocol)
        && (Port is not int port || d.SourcePort == port || d.DestinationPort == port);
}

/// <summary>A per-datagram choice of delivery and priority (U-0014, U-0034).</summary>
internal sealed record PolicyRule(PacketMatch Match, DeliveryKind? Mode, byte? Priority);

/// <summary>One entry of the subnet-based address table (U-0024).</summary>
internal sealed record RouteEntry(IPNetwork Prefix, SisAddress Node);

/// <summary>The s5066-ip configuration file, JSON with // comments.</summary>
internal sealed record IpConfig
{
    /// <summary>The SAP the client binds (U-0012).</summary>
    public const byte Sap = 9;

    /// <summary>Default priority of a datagram no rule names: 6, the IP client's default in [Ed4 F.3, Table F-2, p.F-2] (interp U-0038).</summary>
    public const byte DefaultPriority = 6;

    /// <summary>Default priority of ICMP and ICMPv6 (U-0035 item 4, interp U-0038).</summary>
    public const byte DefaultIcmpPriority = 8;

    /// <summary>Default length of the queue held while the node has flow off (interp U-0040).</summary>
    public const int DefaultQueueLimit = 32;

    public string SisHost { get; init; } = "127.0.0.1";

    public int SisPort { get; init; } = 5066;

    public string Device { get; init; } = "s5066";

    public IReadOnlyList<RouteEntry> Routes { get; init; } = [];

    public SisAddress? DefaultNode { get; init; }

    public SisAddress? MulticastGroup { get; init; }

    public byte MulticastRepeats { get; init; } = 1;

    public DeliveryKind Mode { get; init; } = DeliveryKind.Arq;

    public byte Priority { get; init; } = DefaultPriority;

    public byte IcmpPriority { get; init; } = DefaultIcmpPriority;

    public IReadOnlyList<PolicyRule> Rules { get; init; } = [];

    /// <summary>Null allows every datagram (interp U-0044).</summary>
    public IReadOnlyList<PacketMatch>? Allow { get; init; }

    public int QueueLimit { get; init; } = DefaultQueueLimit;

    public static IpConfig Load(string path) => Parse(File.ReadAllText(path));

    public static IpConfig Parse(string json)
    {
        JsonObject o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new IpConfigException("the file is not a JSON object");
        string[] known = ["sis", "device", "routes", "default_node", "multicast", "mode", "priority", "icmp_priority", "rules", "allow", "queue_limit"];
        foreach (KeyValuePair<string, JsonNode?> kv in o)
        {
            if (!known.Contains(kv.Key))
            {
                throw new IpConfigException($"unknown key {kv.Key}");
            }
        }

        var c = new IpConfig();
        if (Str(o, "sis") is { } sis)
        {
            int colon = sis.LastIndexOf(':');
            if (colon < 1 || !int.TryParse(sis[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            {
                throw new IpConfigException($"sis: not host:port: {sis}");
            }

            c = c with { SisHost = sis[..colon].Trim('[', ']'), SisPort = port };
        }

        if (Str(o, "device") is { } dev)
        {
            if (dev.Length is 0 or > 15)
            {
                throw new IpConfigException("device: a name of 1 to 15 characters");
            }

            c = c with { Device = dev };
        }

        var routes = new List<RouteEntry>();
        if (o["routes"] is JsonArray ra)
        {
            foreach (JsonNode? n in ra)
            {
                if (n is not JsonObject r || Str(r, "prefix") is not { } p || Str(r, "node") is not { } node)
                {
                    throw new IpConfigException("routes: each entry needs prefix and node");
                }

                SisAddress a = Address(node, "routes.node");
                if (a.Group)
                {
                    throw new IpConfigException($"routes.node: {node} is a group address; unicast routes go to one node");
                }

                routes.Add(new RouteEntry(Prefix(p, "routes.prefix"), a));
            }
        }
        else if (o["routes"] is not null)
        {
            throw new IpConfigException("routes: a list");
        }

        c = c with { Routes = routes };
        if (Str(o, "default_node") is { } dn)
        {
            c = c with { DefaultNode = Address(dn, "default_node") };
        }

        if (o["multicast"] is JsonObject m)
        {
            if (Str(m, "group") is not { } g)
            {
                throw new IpConfigException("multicast: needs group");
            }

            c = c with { MulticastGroup = Address(g, "multicast.group"), MulticastRepeats = (byte)Int(m, "repeats", 0, 15, 1) };
        }
        else if (o["multicast"] is not null)
        {
            throw new IpConfigException("multicast: an object with group and repeats");
        }

        if (Str(o, "mode") is { } mode)
        {
            c = c with { Mode = ParseMode(mode, "mode") };
        }

        c = c with
        {
            Priority = (byte)Int(o, "priority", 0, 15, DefaultPriority),
            IcmpPriority = (byte)Int(o, "icmp_priority", 0, 15, DefaultIcmpPriority),
            QueueLimit = Int(o, "queue_limit", 1, 10000, DefaultQueueLimit),
        };

        var rules = new List<PolicyRule>();
        if (o["rules"] is JsonArray rl)
        {
            foreach (JsonNode? n in rl)
            {
                if (n is not JsonObject r)
                {
                    throw new IpConfigException("rules: each entry is an object");
                }

                PacketMatch match = Match(r, "rules");
                DeliveryKind? k = Str(r, "mode") is { } s ? ParseMode(s, "rules.mode") : null;
                byte? pri = r["priority"] is null ? null : (byte)Int(r, "priority", 0, 15, 0);
                if (k is null && pri is null)
                {
                    throw new IpConfigException("rules: each entry needs mode or priority");
                }

                rules.Add(new PolicyRule(match, k, pri));
            }
        }

        c = c with { Rules = rules };
        if (o["allow"] is JsonArray al)
        {
            c = c with { Allow = [.. al.Select(n => n is JsonObject a ? Match(a, "allow") : throw new IpConfigException("allow: each entry is an object"))] };
        }

        return c;
    }

    private static PacketMatch Match(JsonObject r, string where)
    {
        IPNetwork? prefix = Str(r, "prefix") is { } p ? Prefix(p, where + ".prefix") : null;
        int? proto = Str(r, "protocol") is { } s
            ? s switch
            {
                "icmp" => IpDatagram.Icmp,
                "tcp" => IpDatagram.Tcp,
                "udp" => IpDatagram.Udp,
                "icmpv6" => IpDatagram.Icmpv6,
                _ when int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n <= 255 => n,
                _ => throw new IpConfigException($"{where}.protocol: icmp, icmpv6, tcp, udp or a number 0 to 255, not {s}"),
            }
            : null;
        int? port = r["port"] is null ? null : Int(r, "port", 0, 65535, 0);
        return new PacketMatch(prefix, proto, port);
    }

    private static string? Str(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => throw new IpConfigException($"{key}: a string"),
    };

    private static int Int(JsonObject o, string key, int min, int max, int fallback) => o[key] switch
    {
        null => fallback,
        JsonValue v when v.TryGetValue(out int i) && i >= min && i <= max => i,
        _ => throw new IpConfigException($"{key}: a whole number {min} to {max}"),
    };

    private static DeliveryKind ParseMode(string s, string key) => s switch
    {
        "arq" => DeliveryKind.Arq,
        "non_arq" => DeliveryKind.NonArq,
        _ => throw new IpConfigException($"{key}: arq or non_arq, not {s}"),
    };

    private static IPNetwork Prefix(string s, string key) =>
        IPNetwork.TryParse(s, out IPNetwork n) ? n : throw new IpConfigException($"{key}: not a prefix such as 10.66.2.0/24 or fd66:2::/64: {s}");

    /// <summary>
    /// Parses "w.x.y.z": w 0 to 15 is a node, 16 to 31 a group with the group flag as a 29th bit, so
    /// 31.255.255.255 is the broadcast address (C-0122, C-0123), as s5066-test writes them.
    /// </summary>
    internal static SisAddress Address(string text, string key)
    {
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 1 && int.TryParse(text[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w <= 31)
        {
            bool ok = w < 16
                ? SisAddress.TryParse(text, false, 7, out SisAddress a)
                : SisAddress.TryParse((w - 16).ToString(CultureInfo.InvariantCulture) + text[dot..], true, 7, out a);
            if (ok)
            {
                return a;
            }
        }

        throw new IpConfigException($"{key}: not a 5066 address (w.x.y.z, w 16 to 31 for a group): {text}");
    }

    /// <summary>An address as an operator reads it.</summary>
    internal static string Show(SisAddress a) => a.Group
        ? string.Create(CultureInfo.InvariantCulture, $"{16 + ((a.Value >> 24) & 15)}.{(a.Value >> 16) & 255}.{(a.Value >> 8) & 255}.{a.Value & 255}")
        : a.Dotted;
}
