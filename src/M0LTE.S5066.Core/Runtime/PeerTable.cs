using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>What this node knows of a peer's edition (C-0043 to C-0045).</summary>
internal enum KnownEdition
{
    /// <summary>Not learned yet: no Ed4 capability is used towards it (C-0044), and its D_PDUs are decoded as Ed4 (core-architecture.md 10 item 8).</summary>
    Unknown,

    /// <summary>Edition 3: Ed4 capabilities are not used towards it (C-0044), and types 9 to 14 from it are undefined (C-0051, C-0388).</summary>
    Ed3,

    /// <summary>Edition 4 or later (C-0050, C-0054).</summary>
    Ed4,
}

/// <summary>
/// The per-peer facts the CAS learns by CAS-1 negotiation and the DTS and planner read
/// (core-architecture.md 4.2). Only the CAS writes it. Keyed by address in a sorted dictionary, so
/// any enumeration is in address order (core-architecture.md 6 rule 3).
/// </summary>
/// <remarks>
/// The edition is recorded from link setup (B-0227 to B-0231, B-0408) and kept after a link ends,
/// so later links can use it (B-0231). Editions are discovered by CAS-1 negotiation only (C-0046
/// not used; C-0055).
/// </remarks>
internal sealed class PeerTable
{
    private readonly SortedDictionary<uint, KnownEdition> _editions = new();

    /// <summary>The recorded edition, <see cref="KnownEdition.Unknown"/> for a peer never negotiated with.</summary>
    public KnownEdition EditionOf(uint peer) => _editions.TryGetValue(peer, out KnownEdition e) ? e : KnownEdition.Unknown;

    /// <summary>Records the edition (B-0408). CAS only.</summary>
    public void SetEdition(uint peer, KnownEdition edition) => _editions[peer] = edition;

    /// <summary>The edition a peer's D_PDUs are decoded with: Ed3 only for a peer recorded Ed3, else Ed4 (core-architecture.md 10 item 8).</summary>
    public PeerEdition DecodeEditionOf(uint peer) => EditionOf(peer) == KnownEdition.Ed3 ? PeerEdition.Ed3 : PeerEdition.Ed4;
}
