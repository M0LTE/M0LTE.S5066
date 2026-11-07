using System.Globalization;
using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// The timers planned for phases 3 to 6 (core-architecture.md 3.2). Each is registered in
/// corpus-format.md section 4.6 by the item that first starts it. The numeric order of this enum
/// is the first key of <see cref="TimerName"/>'s total order; protocol code never depends on it
/// (core-architecture.md 3.3, no enumeration of the timer table).
/// </summary>
public enum TimerKind
{
    /// <summary>csma.lbt_wait.</summary>
    CsmaLbtWait,

    /// <summary>csma.cont_wait.</summary>
    CsmaContWait,

    /// <summary>csma.vdcd_end.</summary>
    CsmaVdcdEnd,

    /// <summary>dts.ack_wait/&lt;peer&gt;.</summary>
    DtsAckWait,

    /// <summary>dts.management_wait/&lt;peer&gt;.</summary>
    DtsManagementWait,

    /// <summary>dts.ttd.</summary>
    DtsTtd,

    /// <summary>dts.reception_window.</summary>
    DtsReceptionWindow,

    /// <summary>cas.link_request/&lt;peer&gt;.</summary>
    CasLinkRequest,

    /// <summary>cas.first_data/&lt;peer&gt;.</summary>
    CasFirstData,

    /// <summary>cas.break/&lt;peer&gt;.</summary>
    CasBreak,

    /// <summary>cas.clean_close/&lt;peer&gt;.</summary>
    CasCleanClose,

    /// <summary>sis.confirm_wait.</summary>
    SisConfirmWait,

    /// <summary>sis.ttd.</summary>
    SisTtd,

    /// <summary>sis.soft_link_idle/&lt;peer&gt;.</summary>
    SisSoftLinkIdle,

    /// <summary>sis.soft_link_min_hold/&lt;peer&gt;.</summary>
    SisSoftLinkMinHold,

    /// <summary>sis.soft_link_max/&lt;peer&gt;.</summary>
    SisSoftLinkMax,
}

/// <summary>The sublayer that owns a timer and receives its expiry.</summary>
internal enum Sublayer
{
    Sis,
    Cas,
    Dts,
    Mac,
}

/// <summary>What qualifier a timer kind takes in its trace name.</summary>
internal enum TimerQualifier
{
    None,
    Peer,
    Conn,
}

/// <summary>
/// A timer name: the kind plus its qualifier, a peer address or a conn number, or neither
/// (core-architecture.md 3.2). It has a total order, (Kind, Peer, Conn) with absent before
/// present, so a <see cref="SortedDictionary{TKey, TValue}"/> keyed by it enumerates identically
/// in C# and C++.
/// </summary>
/// <param name="Kind">The timer kind.</param>
/// <param name="Peer">The peer address qualifier, for kinds that take one.</param>
/// <param name="Conn">The conn qualifier, for kinds that take one.</param>
public readonly record struct TimerName(TimerKind Kind, uint? Peer = null, int? Conn = null) : IComparable<TimerName>
{
    /// <summary>A timer with no qualifier.</summary>
    /// <param name="kind">The kind.</param>
    public static TimerName Of(TimerKind kind) => new(kind);

    /// <summary>A per-peer timer.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="peer">The peer's 28-bit address.</param>
    public static TimerName ForPeer(TimerKind kind, uint peer) => new(kind, peer);

    /// <inheritdoc/>
    public int CompareTo(TimerName other)
    {
        int c = ((int)Kind).CompareTo((int)other.Kind);
        if (c != 0)
        {
            return c;
        }

        c = CompareNullable(Peer, other.Peer);
        return c != 0 ? c : CompareNullable(Conn, other.Conn);
    }

    /// <summary>The trace form, <c>&lt;sublayer&gt;.&lt;timer&gt;[/&lt;qualifier&gt;]</c> (corpus-format.md 4.6).</summary>
    public string ToTraceString()
    {
        Validate();
        string name = BaseName(Kind);
        if (Peer is uint peer)
        {
            return name + "/" + DpduAddress.ToDotted(peer);
        }

        return Conn is int conn ? name + "/" + conn.ToString(CultureInfo.InvariantCulture) : name;
    }

    /// <inheritdoc/>
    public override string ToString() => ToTraceString();

    /// <summary>Parses a trace name. Harness side only: no protocol decision parses a string.</summary>
    /// <param name="s">The trace name.</param>
    /// <exception cref="FormatException">Not a planned timer name with the right qualifier.</exception>
    public static TimerName Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int slash = s.IndexOf('/', StringComparison.Ordinal);
        string baseName = slash < 0 ? s : s[..slash];
        TimerKind? kind = null;
        for (int k = 0; k <= (int)TimerKind.SisSoftLinkMax; k++)
        {
            if (string.Equals(BaseName((TimerKind)k), baseName, StringComparison.Ordinal))
            {
                kind = (TimerKind)k;
                break;
            }
        }

        if (kind is null)
        {
            throw new FormatException($"'{s}' is not a planned timer name");
        }

        TimerName name;
        if (slash < 0)
        {
            name = new TimerName(kind.Value);
        }
        else
        {
            string q = s[(slash + 1)..];
            if (q.Contains('.', StringComparison.Ordinal))
            {
                name = DpduAddress.TryParse(q, out uint peer)
                    ? new TimerName(kind.Value, peer)
                    : throw new FormatException($"'{s}' has a bad address qualifier");
            }
            else
            {
                name = int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out int conn) && conn > 0
                    ? new TimerName(kind.Value, Conn: conn)
                    : throw new FormatException($"'{s}' has a bad conn qualifier");
            }
        }

        if (!name.IsWellFormed)
        {
            throw new FormatException($"'{s}' has the wrong qualifier for its kind");
        }

        return name;
    }

    /// <summary>The sublayer that owns this timer (core-architecture.md 4.2).</summary>
    internal Sublayer Owner => Kind switch
    {
        TimerKind.CsmaLbtWait or TimerKind.CsmaContWait or TimerKind.CsmaVdcdEnd => Sublayer.Mac,
        TimerKind.DtsAckWait or TimerKind.DtsManagementWait or TimerKind.DtsTtd or TimerKind.DtsReceptionWindow => Sublayer.Dts,
        TimerKind.CasLinkRequest or TimerKind.CasFirstData or TimerKind.CasBreak or TimerKind.CasCleanClose => Sublayer.Cas,
        TimerKind.SisConfirmWait or TimerKind.SisTtd or TimerKind.SisSoftLinkIdle or TimerKind.SisSoftLinkMinHold
            or TimerKind.SisSoftLinkMax => Sublayer.Sis,
        _ => throw new InvalidOperationException($"timer kind {(int)Kind} has no owner"),
    };

    /// <summary>True when the qualifier present matches what the kind takes.</summary>
    internal bool IsWellFormed => Qualifier(Kind) switch
    {
        TimerQualifier.None => Peer is null && Conn is null,
        TimerQualifier.Peer => Peer is uint p && p <= DpduAddress.MaxValue && Conn is null,
        _ => Conn is int c && c > 0 && Peer is null,
    };

    /// <summary>Throws when the name is not well formed: building one is a bug, never a protocol event.</summary>
    internal void Validate()
    {
        if (!IsWellFormed)
        {
            throw new InvalidOperationException($"timer kind {Kind} has the wrong qualifier");
        }
    }

    internal static TimerQualifier Qualifier(TimerKind kind) => kind switch
    {
        TimerKind.DtsAckWait or TimerKind.DtsManagementWait or TimerKind.CasLinkRequest or TimerKind.CasFirstData
            or TimerKind.CasBreak or TimerKind.CasCleanClose or TimerKind.SisSoftLinkIdle
            or TimerKind.SisSoftLinkMinHold or TimerKind.SisSoftLinkMax => TimerQualifier.Peer,
        _ => TimerQualifier.None,
    };

    /// <summary>The registered <c>&lt;sublayer&gt;.&lt;timer&gt;</c> part, from an explicit table.</summary>
    internal static string BaseName(TimerKind kind) => kind switch
    {
        TimerKind.CsmaLbtWait => "csma.lbt_wait",
        TimerKind.CsmaContWait => "csma.cont_wait",
        TimerKind.CsmaVdcdEnd => "csma.vdcd_end",
        TimerKind.DtsAckWait => "dts.ack_wait",
        TimerKind.DtsManagementWait => "dts.management_wait",
        TimerKind.DtsTtd => "dts.ttd",
        TimerKind.DtsReceptionWindow => "dts.reception_window",
        TimerKind.CasLinkRequest => "cas.link_request",
        TimerKind.CasFirstData => "cas.first_data",
        TimerKind.CasBreak => "cas.break",
        TimerKind.CasCleanClose => "cas.clean_close",
        TimerKind.SisConfirmWait => "sis.confirm_wait",
        TimerKind.SisTtd => "sis.ttd",
        TimerKind.SisSoftLinkIdle => "sis.soft_link_idle",
        TimerKind.SisSoftLinkMinHold => "sis.soft_link_min_hold",
        TimerKind.SisSoftLinkMax => "sis.soft_link_max",
        _ => throw new InvalidOperationException($"timer kind {(int)kind} has no name"),
    };

    private static int CompareNullable<T>(T? a, T? b)
        where T : struct, IComparable<T>
    {
        if (a is null)
        {
            return b is null ? 0 : -1;
        }

        return b is null ? 1 : a.Value.CompareTo(b.Value);
    }
}
