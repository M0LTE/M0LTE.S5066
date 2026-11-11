using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Ip;

/// <summary>Why a datagram was not sent or not delivered; each has a counter.</summary>
internal enum DropReason
{
    /// <summary>From the tun device, but not one whole IPv4 or IPv6 datagram.</summary>
    NotIp,

    /// <summary>The allow-list does not name it (U-0035 item 3, interp U-0044).</summary>
    NotAllowed,

    /// <summary>No route and no default node maps its destination (interp U-0043).</summary>
    NoRoute,

    /// <summary>A multicast destination with no multicast group configured (U-0005, interp U-0042).</summary>
    Multicast,

    /// <summary>The queue held while flow was off was full (interp U-0040).</summary>
    QueueFull,

    /// <summary>Over the MTU and the client may not answer with ICMP (interp U-0037).</summary>
    TooBig,

    /// <summary>Received from SAP 9 but not one whole IPv4 or IPv6 datagram (interp U-0041).</summary>
    ReceivedNotIp,
}

/// <summary>What to do with one datagram from the tun device.</summary>
/// <param name="Destination">The 5066 node or group.</param>
/// <param name="Mode">The delivery mode of this datagram's S_UNIDATA_REQUEST (U-0016).</param>
/// <param name="Priority">The SIS priority, 0 to 15 (A-0063).</param>
internal sealed record SendDecision(SisAddress Destination, ServiceType Mode, byte Priority);

/// <summary>
/// The per-datagram choices of U.8 [Ed4 U.8, p.U-6 to U-7]: filtering, destination node, ARQ or
/// non-ARQ and priority, from the destination address, the IP protocol and the port.
/// </summary>
internal sealed class Policy(IpConfig config)
{
    /// <summary>ARQ with node delivery confirmation, in order (U-0018, interp U-0039).</summary>
    public static readonly ServiceType Arq = new(1, Confirm: 1, Order: 0);

    /// <summary>Non-ARQ, no confirmation, as they arrive (U-0021); repeats as given (U-0022).</summary>
    public static ServiceType NonArq(byte repeats) => new(2, Confirm: 0, Order: 1, MinRetx: repeats);

    /// <summary>The service type of the bind: ARQ, the most likely traffic (U-0015, interp U-0039).</summary>
    public static readonly ServiceType BindServiceType = Arq;

    /// <summary>Decides for one parsed datagram: a send, or the reason it is dropped.</summary>
    public (SendDecision? Send, DropReason? Drop) Decide(IpDatagram d)
    {
        if (config.Allow is { } allow && !allow.Any(a => a.Matches(d)))
        {
            return (null, DropReason.NotAllowed);
        }

        PolicyRule? rule = config.Rules.FirstOrDefault(r => r.Match.Matches(d));
        bool icmp = d.Protocol is IpDatagram.Icmp or IpDatagram.Icmpv6;
        byte priority = rule?.Priority ?? (icmp ? config.IcmpPriority : config.Priority);

        if (d.IsMulticast)
        {
            // Multicast only to the configured group and only non-ARQ (U-0005, U-0019, interp U-0042).
            return config.MulticastGroup is { } g
                ? (new SendDecision(g, NonArq(config.MulticastRepeats), priority), null)
                : (null, DropReason.Multicast);
        }

        SisAddress? node = Route(d.Destination);
        if (node is not { } dest)
        {
            return (null, DropReason.NoRoute);
        }

        DeliveryKind kind = rule?.Mode ?? config.Mode;

        // A group default node takes non-ARQ only, as the node rejects ARQ to a group (interp S-0138).
        ServiceType mode = kind == DeliveryKind.Arq && !dest.Group ? Arq : NonArq(0);
        return (new SendDecision(dest, mode, priority), null);
    }

    /// <summary>Longest prefix match over the routes, else the default node (U-0024).</summary>
    public SisAddress? Route(System.Net.IPAddress destination)
    {
        RouteEntry? best = null;
        foreach (RouteEntry r in config.Routes)
        {
            if (r.Prefix.BaseAddress.AddressFamily == destination.AddressFamily && r.Prefix.Contains(destination)
                && (best is null || r.Prefix.PrefixLength > best.Prefix.PrefixLength))
            {
                best = r;
            }
        }

        return best?.Node ?? config.DefaultNode;
    }
}
