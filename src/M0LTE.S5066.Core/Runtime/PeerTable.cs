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
/// The per-peer facts the CAS learns by CAS-1 negotiation, and the DTS from a received CAPABILITY
/// EOW, and the DTS and planner read (core-architecture.md 4.2). Keyed by address in a sorted
/// dictionary, so any enumeration is in address order (core-architecture.md 6 rule 3).
/// </summary>
/// <remarks>
/// The edition is recorded from link setup (B-0227 to B-0231, B-0408) and kept after a link ends,
/// so later links can use it (B-0231). Since P7-3 a CAPABILITY EOW with its Ed4 bit set, received
/// in a valid header addressed to our individual address, also records its source Ed4, but only
/// where CAS-1 has not spoken: a CAS-1 record always wins and a later CAS-1 negotiation overwrites
/// a CAPABILITY record (C-0045, C-0049, interp C-0055, MAIN-0008, interp MAIN-0009; C-0046 a
/// priori knowledge is not used).
/// </remarks>
internal sealed class PeerTable
{
    private readonly SortedDictionary<uint, (KnownEdition Edition, bool ByCas1)> _editions = new();

    /// <summary>The recorded edition, <see cref="KnownEdition.Unknown"/> for a peer never negotiated with and never heard announce Ed4.</summary>
    public KnownEdition EditionOf(uint peer) => _editions.TryGetValue(peer, out var e) ? e.Edition : KnownEdition.Unknown;

    /// <summary>True when the edition was recorded by CAS-1 negotiation (B-0408), for tests.</summary>
    public bool RecordedByCas1(uint peer) => _editions.TryGetValue(peer, out var e) && e.ByCas1;

    /// <summary>Records the edition from CAS-1 negotiation (B-0408), replacing any record. CAS only.</summary>
    public void SetEdition(uint peer, KnownEdition edition) => _editions[peer] = (edition, true);

    /// <summary>
    /// A CAPABILITY EOW with the Ed4 bit set came from the peer (interp C-0055, interp MAIN-0009):
    /// records it Ed4 unless CAS-1 has recorded an edition for it. DTS only.
    /// </summary>
    public void RecordCapabilityEd4(uint peer)
    {
        if (!RecordedByCas1(peer))
        {
            _editions[peer] = (KnownEdition.Ed4, false);
        }
    }

    /// <summary>The edition a peer's D_PDUs are decoded with: Ed3 only for a peer recorded Ed3, else Ed4 (core-architecture.md 10 item 8).</summary>
    public PeerEdition DecodeEditionOf(uint peer) => EditionOf(peer) == KnownEdition.Ed3 ? PeerEdition.Ed3 : PeerEdition.Ed4;
}
