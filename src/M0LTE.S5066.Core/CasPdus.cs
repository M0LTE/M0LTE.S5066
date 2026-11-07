namespace M0LTE.S5066.Core;

/// <summary>
/// A CAS-1 C_PDU, the peer protocol of Annex B (types 0 to 10) [Ed4 B.7.1, pp.B-13 to B-25].
/// Immutable. Decode with <see cref="CpduCodec.Decode"/>, encode with <see cref="CpduCodec.Encode"/>.
/// </summary>
/// <remarks>
/// The Type is the high nibble of octet 0 (B-0105, B-0106). Types 7 to 10 are ALE only and out of
/// profile (B-0104, B-0176 to B-0189): they decode so a node can discard them knowingly, and are
/// never encoded. Types 11 to 15 are undefined (B-0305).
/// </remarks>
public abstract record CPdu
{
    /// <summary>The 4-bit C_PDU type.</summary>
    public abstract byte Type { get; }
}

/// <summary>Type 0 DATA: carries one S_PDU and nothing else (B-0111 to B-0114) [Ed4 B.7.1.1, Fig B-1, p.B-14].</summary>
/// <param name="SPdu">The encapsulated S_PDU bytes.</param>
public sealed record CpduData(ReadOnlyMemory<byte> SPdu) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 0;

    /// <inheritdoc/>
    public bool Equals(CpduData? other) => other is not null && SPdu.Span.SequenceEqual(other.SPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(SPdu.Span);
        return h.ToHashCode();
    }
}

/// <summary>
/// Type 1 PHYSICAL LINK REQUEST: one octet with Short (bit 2), Ed4 (bit 1) and LINK (bit 0)
/// (B-0117, B-0122 to B-0125, B-0127, B-0307) [Ed4 B.7.1.2, Fig B-2, p.B-15].
/// </summary>
/// <param name="Short">Ask for the 8-bit FSN D_PDU family; only sent together with Ed4 (B-0125).</param>
/// <param name="Ed4">The sender supports Edition 4 or later (B-0122).</param>
/// <param name="Link">The HARD LINKS bit; never sent by this node (B-0126, B-0127), decoded so a request with it set can be rejected (B-0128).</param>
public sealed record PhysicalLinkRequest(bool Short, bool Ed4, bool Link) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 1;
}

/// <summary>
/// Type 2 PHYSICAL LINK ACCEPTED: the same one-octet layout as the request (B-0131, B-0138 to
/// B-0142) [Ed4 B.7.1.3, Fig B-3, p.B-17].
/// </summary>
/// <param name="Short">Short-FSN D_PDUs are used on the link (only with Ed4, B-0140).</param>
/// <param name="Ed4">Echo of the request's Ed4 bit (B-0138, B-0139).</param>
/// <param name="Link">The HARD LINKS bit; sent as 0 (B-0137).</param>
public sealed record PhysicalLinkAccepted(bool Short, bool Ed4, bool Link) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 2;
}

/// <summary>
/// Type 3 PHYSICAL LINK REJECTED: Type and a 4-bit Reason in the low nibble (B-0144, B-0147,
/// B-0148) [Ed4 B.7.1.4, Fig B-4, p.B-18]. Reasons: 0 unknown, 1 broadcast-only node, 2 higher
/// priority request pending, 4 too many active links, 5 exclusive links not supported; 3 and
/// 6 to 15 are unspecified. The codec carries any value 0 to 15.
/// </summary>
/// <param name="Reason">The 4-bit reject reason.</param>
public sealed record PhysicalLinkRejected(byte Reason) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 3;
}

/// <summary>
/// Type 4 PHYSICAL LINK BREAK: Type and a 4-bit Reason in the low nibble (B-0153, B-0156,
/// B-0157, B-0302, B-0309) [Ed4 B.7.1.5, Fig B-5, p.B-19]. On receipt any value is accepted.
/// </summary>
/// <param name="Reason">The 4-bit break reason.</param>
public sealed record PhysicalLinkBreak(byte Reason) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 4;
}

/// <summary>Type 5 PHYSICAL LINK BREAK CONFIRM: one octet, low nibble unused (B-0161, B-0164) [Ed4 B.7.1.6, Fig B-6, p.B-21].</summary>
public sealed record PhysicalLinkBreakConfirm : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 5;
}

/// <summary>
/// Type 6 PHYSICAL LINK BREAK REQUEST: two octets; octet 0 has a 3-bit Reason in bits 2 to 0, octet 1
/// is the Priority (B-0167, B-0171 to B-0173, B-0311) [Ed4 B.7.1.7, Fig B-7, p.B-22]. Reasons:
/// 0 no data, 1 higher priority, 2 channel share, 3 operator request, 4 to 7 reserved (B-0320).
/// </summary>
/// <param name="Reason">The 3-bit reason.</param>
/// <param name="Priority">Octet 1, carried raw. It holds the higher-priority traffic's priority when Reason is 1, and 0 otherwise (B-0172, B-0173).</param>
public sealed record PhysicalLinkBreakRequest(byte Reason, byte Priority) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 6;
}

/// <summary>Type 7 PHYSICAL LINK RESUME, ALE only, decode only (B-0176) [Ed4 B.7.1.8, Fig B-8, p.B-23].</summary>
public sealed record PhysicalLinkResume : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 7;
}

/// <summary>Type 8 PHYSICAL LINK RESUME ACCEPT, ALE only, decode only (B-0179, B-0304) [Ed4 B.7.1.9, Fig B-9, p.B-23].</summary>
public sealed record PhysicalLinkResumeAccept : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 8;
}

/// <summary>Type 9 PHYSICAL LINK RESUME REJECT, ALE only, decode only (B-0182) [Ed4 B.7.1.10, Fig B-10, p.B-24].</summary>
public sealed record PhysicalLinkResumeReject : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 9;
}

/// <summary>
/// Type 10 ALE RELINK, ALE only, decode only: a 3-bit Reason in bits 2 to 0 (B-0185, B-0188,
/// B-0304) [Ed4 B.7.1.11, Fig B-11, p.B-24]. 0 ALE quality, 1 ALE time.
/// </summary>
/// <param name="Reason">The 3-bit reason.</param>
public sealed record AleRelink(byte Reason) : CPdu
{
    /// <inheritdoc/>
    public override byte Type => 10;
}
