using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Runtime;

/// <summary>
/// Chooses the EOW of each D_PDU header the planner places (core-architecture.md 9, phase 7, P7-3
/// and open question 1; interp C-1260). A header holds one EOW (C-0098), chosen per individual
/// peer. In each transmission the headers to a peer recorded Ed4 take, in order: an owed
/// UNRECOGNIZED-TYPE-ERROR (C-1079, once), the top-priority recommendations of Table C-25 (C-0949),
/// SENDER-APPROACH when the transmission carries data to the peer, and then the next entries of a
/// per-peer rotation through the repeating set that persists across transmissions (C-0947); when
/// the set is empty, CAPABILITY with the Ed4 bit set and Duplex clear (C-0426, C-0489 to C-0491).
/// PADDING in the fill draws from the same sequence, so it continues the rotation (C-0373, interp
/// C-1154).
/// </summary>
/// <remarks>
/// <para>
/// D_PDUs to a group or broadcast address carry EMPTY (C-0937); so do D_PDUs to a peer of unknown
/// edition, since Ed4 EOWs are not used where the edition is unknown (C-0419, interp C-0055). A peer
/// recorded Ed3 never gets an Ed4 EOW (C-0419, C-0938): it gets ED3-BASIC-RATE while a
/// recommendation exists (C-0957, interp C-1273), else EMPTY. An owed
/// UNRECOGNIZED-TYPE-ERROR, an All-editions EOW (C-0401, C-0415), still goes first to an individual
/// peer of any edition (C-1079). A peer recorded Ed4 never gets ED3-BASIC-RATE (C-0417, C-0958).
/// We never send ED3-CAPABILITY (C-1019) or SPEED-USED (C-0482, C-0945), and never originate a
/// MANAGEMENT D_PDU, so never one with EOW 1 or 2 (C-0918).
/// </para>
/// <para>
/// The top-priority recommendations, the repeating set and the Ed3 recommendation are filled by
/// P7-6 (<see cref="Recommender"/>, interp C-1271, C-1273), SENDER-APPROACH by P7-7. With an empty
/// drc.table they stay empty and the only Ed4 EOW sent is CAPABILITY. The providers are settable so
/// unit tests can show the order with synthetic content.
/// </para>
/// </remarks>
internal sealed class EowScheduler
{
    /// <summary>CAPABILITY: Ed4 bit 1, Duplex 0 (C-0488 to C-0491).</summary>
    internal static readonly int Capability = EowCodec.Encode(new CapabilityEow(false, true));

    private readonly PeerTable _editions;
    private readonly SortedDictionary<uint, int> _owed = new();
    private readonly SortedDictionary<uint, int> _rotation = new();
    private readonly SortedDictionary<uint, Queue<Eow>> _head = new();
    private IReadOnlyCollection<uint> _dataPeers = [];

    public EowScheduler(PeerTable editions)
    {
        _editions = editions;
    }

    /// <summary>The top-priority recommendation EOWs for a peer, in order (Table C-25; P7-6). Empty in P7-3.</summary>
    public Func<uint, IReadOnlyList<Eow>> TopPriority { get; set; } = _ => [];

    /// <summary>The repeating set for a peer, in rotation order (open question 1; P7-6). Empty in P7-3.</summary>
    public Func<uint, IReadOnlyList<Eow>> RepeatingSet { get; set; } = _ => [];

    /// <summary>ED3-BASIC-RATE for a peer recorded Ed3 (C-0957; P7-6), or null for EMPTY.</summary>
    public Func<uint, Eow?> Ed3Recommendation { get; set; } = _ => null;

    /// <summary>Our SENDER-APPROACH for a peer the transmission carries data to (P7-7), or null. Null in P7-3.</summary>
    public Func<uint, Eow?> SenderApproach { get; set; } = _ => null;

    /// <summary>
    /// An EOW owed to a peer: UNRECOGNIZED-TYPE-ERROR (C-0445). It goes in the next D_PDU placed for
    /// that individual address; a later one replaces an earlier one still unsent (C-1079).
    /// </summary>
    public void Owe(uint peer, int eow) => _owed[peer] = eow;

    /// <summary>The EOW owed to a peer and not yet sent, or null.</summary>
    public int? OwedFor(uint peer) => _owed.TryGetValue(peer, out int eow) ? eow : null;

    /// <summary>
    /// Starts a transmission: the per-transmission head (top priority, then SENDER-APPROACH) is
    /// built afresh for each peer on its first header, and the rotation carries on.
    /// </summary>
    /// <param name="dataPeers">The individual peers this transmission carries data to.</param>
    public void BeginTransmission(IReadOnlyCollection<uint> dataPeers)
    {
        _head.Clear();
        _dataPeers = dataPeers;
    }

    /// <summary>The 12-bit EOW for the next header placed for this destination.</summary>
    public int Next(uint dst, bool dstIsGroup)
    {
        if (dstIsGroup)
        {
            return 0;   // EMPTY (C-0937, interp C-1079)
        }

        if (_owed.Remove(dst, out int owed))
        {
            return owed;   // C-1079, an All-editions EOW
        }

        KnownEdition edition = _editions.EditionOf(dst);
        if (edition == KnownEdition.Ed3)
        {
            // ED3-BASIC-RATE in every header to the peer when we have a recommendation (C-0957,
            // interp C-1273), else EMPTY; never an Ed4 EOW (C-0419, C-0938).
            return Ed3Recommendation(dst) is Eow ed3 ? EowCodec.Encode(ed3) : 0;
        }

        if (edition != KnownEdition.Ed4)
        {
            return 0;   // EMPTY: unknown edition (C-0419, interp C-0055)
        }

        if (!_head.TryGetValue(dst, out Queue<Eow>? head))
        {
            head = new Queue<Eow>(TopPriority(dst));
            if (_dataPeers.Contains(dst) && SenderApproach(dst) is Eow approach)
            {
                head.Enqueue(approach);
            }

            _head[dst] = head;
        }

        if (head.TryDequeue(out Eow? next))
        {
            return EowCodec.Encode(next);
        }

        IReadOnlyList<Eow> set = RepeatingSet(dst);
        if (set.Count == 0)
        {
            return Capability;   // C-0426
        }

        int i = _rotation.TryGetValue(dst, out int r) ? r % set.Count : 0;
        _rotation[dst] = i + 1;
        return EowCodec.Encode(set[i]);
    }
}

/// <summary>
/// The latest of each recommendation EOW received from one peer in D_PDUs to our individual
/// address (P7-3; C-0459, C-0472, C-0474, C-0477, C-0427; interp C-1262). Each field is replaced by
/// the next usable EOW of its type and is null until one comes. Lives in the peer's DtsPeers entry
/// (core-architecture.md 6 rule 11). Nothing acts on it before P7-7.
/// </summary>
internal sealed class ReceivedEows
{
    /// <summary>MAX-SPEED (C-0459 to C-0461).</summary>
    public SpeedInterleaver? MaxSpeed { get; set; }

    /// <summary>LOW-SPEED (C-0472, C-0473).</summary>
    public SpeedInterleaver? LowSpeed { get; set; }

    /// <summary>MAX-SEGMENT, in units of 4 bytes, 1 to 255 (C-0475, interp C-0476).</summary>
    public int? MaxSegment { get; set; }

    /// <summary>SENDER-APPROACH (C-0478 to C-0482).</summary>
    public SenderApproachEow? SenderApproach { get; set; }

    /// <summary>ED3-BASIC-RATE (C-0427 to C-0441).</summary>
    public Ed3BasicRateEow? Ed3BasicRate { get; set; }
}
