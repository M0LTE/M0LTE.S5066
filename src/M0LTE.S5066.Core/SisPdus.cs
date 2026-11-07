namespace M0LTE.S5066.Core;

/// <summary>
/// A SIS peer S_PDU of Annex A (types 0 to 2) [Ed4 A.3.1, pp.A-14 to A-18]. Immutable. Decode
/// with <see cref="SpduCodec.Decode"/>, encode with <see cref="SpduCodec.Encode"/>.
/// </summary>
/// <remarks>The TYPE is the high nibble of octet 0 (A-0138, A-0139). Types 3 to 15 are reserved (A-0141).</remarks>
public abstract record SPdu
{
    /// <summary>The 4-bit S_PDU type.</summary>
    public abstract byte Type { get; }
}

/// <summary>
/// The TimeToDie of a DATA S_PDU: the Julian day modulo 16 and the GMT seconds with the least
/// significant bit dropped (A-0161 to A-0163) [Ed4 A.3.1.1, p.A-16]. The codec does not resolve
/// it to an instant (A-0166, A-0167).
/// </summary>
/// <param name="DayMod16">Octet 2 bits 3 to 0, 0 to 15.</param>
/// <param name="HalfSeconds">Octets 3 and 4, most significant first.</param>
public readonly record struct SpduTtd(byte DayMod16, ushort HalfSeconds);

/// <summary>
/// Type 0 DATA: a 3-octet S_PCI (5 with a TTD) then the U_PDU (A-0143 to A-0160) [Ed4 A.3.1.1,
/// Figs A-3 and A-4, p.A-15].
/// </summary>
/// <param name="Priority">Octet 0 bits 3 to 0 (A-0147).</param>
/// <param name="SrcSap">Octet 1 bits 7 to 4 (A-0149).</param>
/// <param name="DstSap">Octet 1 bits 3 to 0 (A-0150).</param>
/// <param name="ClientConfirm">Octet 2 bit 7, CLIENT DELIVERY CONFIRM REQUIRED (A-0153).</param>
/// <param name="Ttd">Present exactly when octet 2 bit 6 (VALID TTD) is 1 (A-0155, A-0160).</param>
/// <param name="UPdu">The client data.</param>
public sealed record SpduData(
    byte Priority, byte SrcSap, byte DstSap, bool ClientConfirm, SpduTtd? Ttd, ReadOnlyMemory<byte> UPdu) : SPdu
{
    /// <inheritdoc/>
    public override byte Type => 0;

    /// <inheritdoc/>
    public bool Equals(SpduData? other) =>
        other is not null && Priority == other.Priority && SrcSap == other.SrcSap && DstSap == other.DstSap
        && ClientConfirm == other.ClientConfirm && Ttd == other.Ttd && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Priority);
        h.Add(SrcSap);
        h.Add(DstSap);
        h.Add(ClientConfirm);
        h.Add(Ttd);
        h.AddBytes(UPdu.Span);
        return h.ToHashCode();
    }
}

/// <summary>
/// Type 1 DATA DELIVERY CONFIRM: a 2-octet S_PCI copied from the DATA S_PDU, then a full or
/// partial copy of the U_PDU (A-0169 to A-0175) [Ed4 A.3.1.2, Fig A-6, p.A-17]. The low nibble of
/// octet 0 is the priority (A-0175).
/// </summary>
/// <param name="Priority">Octet 0 bits 3 to 0.</param>
/// <param name="SrcSap">Octet 1 bits 7 to 4, copied unswapped (A-0174).</param>
/// <param name="DstSap">Octet 1 bits 3 to 0.</param>
/// <param name="UPdu">The copy, possibly empty.</param>
public sealed record SpduDeliveryConfirm(byte Priority, byte SrcSap, byte DstSap, ReadOnlyMemory<byte> UPdu) : SPdu
{
    /// <inheritdoc/>
    public override byte Type => 1;

    /// <inheritdoc/>
    public bool Equals(SpduDeliveryConfirm? other) =>
        other is not null && Priority == other.Priority && SrcSap == other.SrcSap && DstSap == other.DstSap
        && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Priority);
        h.Add(SrcSap);
        h.Add(DstSap);
        h.AddBytes(UPdu.Span);
        return h.ToHashCode();
    }
}

/// <summary>
/// Type 2 DATA DELIVERY FAIL: a 2-octet S_PCI then a copy of the U_PDU (A-0177 to A-0185)
/// [Ed4 A.3.1.3, Fig A-8, p.A-18]. The low nibble of octet 0 is the REASON, not a priority (A-0183).
/// </summary>
/// <param name="Reason">Octet 0 bits 3 to 0: 1 is Destination SAP ID not bound; 0 and 2 to 15 are reserved (A-0181). Carried raw.</param>
/// <param name="SrcSap">Octet 1 bits 7 to 4 (A-0182).</param>
/// <param name="DstSap">Octet 1 bits 3 to 0.</param>
/// <param name="UPdu">The copy, possibly empty.</param>
public sealed record SpduDeliveryFail(byte Reason, byte SrcSap, byte DstSap, ReadOnlyMemory<byte> UPdu) : SPdu
{
    /// <inheritdoc/>
    public override byte Type => 2;

    /// <inheritdoc/>
    public bool Equals(SpduDeliveryFail? other) =>
        other is not null && Reason == other.Reason && SrcSap == other.SrcSap && DstSap == other.DstSap
        && UPdu.Span.SequenceEqual(other.UPdu.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Reason);
        h.Add(SrcSap);
        h.Add(DstSap);
        h.AddBytes(UPdu.Span);
        return h.ToHashCode();
    }
}
