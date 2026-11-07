namespace M0LTE.S5066.Core.Sis;

/// <summary>
/// The 12-bit Service Type of S_BIND_REQUEST and the DELIVERY MODE of S_UNIDATA_REQUEST, which
/// share one layout [Ed4 S.5.3, Fig S-6, p.S-11; S.5.16.2, Fig S-20, p.S-19] (matrix S-0079, S-0139).
/// </summary>
/// <remarks>
/// The codec carries every value as sent. Whether a value is acceptable (Transmission Mode 0 in a
/// bind, confirmation with non-ARQ, Extended Field 1 and so on) is node policy, not framing
/// (S-0090 to S-0095, S-0098, S-0150). In a DELIVERY MODE, Transmission Mode 0 means DEFAULT (S-0142).
/// </remarks>
/// <param name="TxMode">Transmission Mode, 4 bits: 0 default or ignore, 1 ARQ, 2 non-ARQ, 3 non-ARQ with errors (S-0080).</param>
/// <param name="Confirm">Delivery Confirmation, 2 bits: 0 none, 1 node, 2 client (S-0081).</param>
/// <param name="Order">Delivery Order, 1 bit: 0 in order, 1 as they arrive (S-0082).</param>
/// <param name="Extended">Extended Field, 1 bit, to be sent as 0 (S-0083, S-0094).</param>
/// <param name="MinRetx">Minimum number of retransmissions, 4 bits (S-0084).</param>
public readonly record struct ServiceType(byte TxMode, byte Confirm = 0, byte Order = 0, byte Extended = 0, byte MinRetx = 0)
{
    /// <summary>The DEFAULT delivery mode: every subfield zero (S-0142, S-0143).</summary>
    public static ServiceType Default => default;

    internal bool IsValid => TxMode <= 15 && Confirm <= 3 && Order <= 1 && Extended <= 1 && MinRetx <= 15;

    /// <summary>The byte holding Transmission Mode, Delivery Confirmation, Delivery Order and Extended Field.</summary>
    internal byte HighByte => (byte)((TxMode << 4) | (Confirm << 2) | (Order << 1) | Extended);

    internal static ServiceType FromBytes(byte high, int minRetx) =>
        new((byte)(high >> 4), (byte)((high >> 2) & 3), (byte)((high >> 1) & 1), (byte)(high & 1), (byte)minRetx);
}
