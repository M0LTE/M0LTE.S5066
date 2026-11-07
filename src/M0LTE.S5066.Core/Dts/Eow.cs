namespace M0LTE.S5066.Core.Dts;

/// <summary>
/// The speed and interleaver byte used by EOW types 8, 9 and 12: interleaver in bits 7-5, speed in
/// bits 4-0 [Ed4 C.6.5.1, Figure C-49, p.C-72] (C-0462).
/// </summary>
/// <param name="Interleaver">0 no recommendation, 1 Ultra Short, 2 Very Short, 3 Short, 4 Medium, 5 Long, 6 Very Long, 7 reserved (C-0469).</param>
/// <param name="Speed">1 to 13 wideband waveform number, 17 to 30 narrowband rates, others reserved (C-0463 to C-0466).</param>
public readonly record struct SpeedInterleaver(int Interleaver, int Speed)
{
    /// <summary>Packs to the byte (C-0462). Every 3-bit and 5-bit value is representable, reserved ones included.</summary>
    public byte ToByte()
    {
        if (Interleaver is < 0 or > 7 || Speed is < 0 or > 31)
        {
            throw new ArgumentException("interleaver is 0 to 7 and speed is 0 to 31");
        }

        return (byte)((Interleaver << 5) | Speed);
    }

    /// <summary>Unpacks the byte (C-0462).</summary>
    public static SpeedInterleaver FromByte(byte b) => new(b >> 5, b & 31);
}

/// <summary>An engineering orderwire message [Ed4 C.6, Table C-4, p.C-66]. One sealed record per structured EOW type.</summary>
public abstract record Eow
{
    /// <summary>The EOW type, 0 to 15 (C-0100).</summary>
    public abstract int Type { get; }
}

/// <summary>Type 0 EMPTY: all-zero contents (C-0425).</summary>
public sealed record EmptyEow : Eow
{
    /// <inheritdoc/>
    public override int Type => 0;
}

/// <summary>Type 1 ED3-BASIC-RATE (C-0427): data rate code, interleaving code, other parameters.</summary>
/// <param name="DataRate">4 bits, Table C-5 (C-0432, C-0433).</param>
/// <param name="Interleaving">2 bits, Table C-6 (C-0435).</param>
/// <param name="OtherParameters">2 bits, Table C-7 (C-0437, C-0438); we send binary 10 (C-0440).</param>
public sealed record Ed3BasicRateEow(int DataRate, int Interleaving, int OtherParameters) : Eow
{
    /// <inheritdoc/>
    public override int Type => 1;
}

/// <summary>Type 3 UNRECOGNIZED-TYPE-ERROR (C-0443): the unrecognised EOW type in bits 3-0.</summary>
public sealed record UnrecognizedTypeErrorEow(int UnrecognizedEowType) : Eow
{
    /// <inheritdoc/>
    public override int Type => 3;
}

/// <summary>Type 4 ED3-CAPABILITY (C-0449, C-0454, C-0455): one boolean per bit, bit 7 first.</summary>
public sealed record Ed3CapabilityEow(
    bool DrcCapable,
    bool Stanag4529,
    bool Ms110_75To2400,
    bool ExtendedRate,
    bool FullDuplex,
    bool SplitFrequency,
    bool NonArcsAle,
    bool Arcs) : Eow
{
    /// <inheritdoc/>
    public override int Type => 4;
}

/// <summary>Type 8 MAX-SPEED (C-0459).</summary>
public sealed record MaxSpeedEow(SpeedInterleaver Value) : Eow
{
    /// <inheritdoc/>
    public override int Type => 8;
}

/// <summary>Type 9 LOW-SPEED (C-0472).</summary>
public sealed record LowSpeedEow(SpeedInterleaver Value) : Eow
{
    /// <inheritdoc/>
    public override int Type => 9;
}

/// <summary>Type 10 MAX-SEGMENT: the recommended segment size is Value x 4 bytes (C-0475).</summary>
public sealed record MaxSegmentEow(int Value) : Eow
{
    /// <inheritdoc/>
    public override int Type => 10;
}

/// <summary>Type 11 SENDER-APPROACH (C-0478): bit 3 EOW 12 needed, bits 2-1 strategy, bit 0 fixed.</summary>
public sealed record SenderApproachEow(bool Eow12Needed, int Strategy, bool Fixed) : Eow
{
    /// <inheritdoc/>
    public override int Type => 11;
}

/// <summary>Type 12 SPEED-USED (C-0485).</summary>
public sealed record SpeedUsedEow(SpeedInterleaver Value) : Eow
{
    /// <inheritdoc/>
    public override int Type => 12;
}

/// <summary>Type 15 CAPABILITY (C-0488): bit 1 duplex, bit 0 Ed4.</summary>
public sealed record CapabilityEow(bool Duplex, bool Ed4) : Eow
{
    /// <inheritdoc/>
    public override int Type => 15;
}

/// <summary>
/// An EOW of a type we do not structure: 2, 5, 6, 7, 13 or 14 (C-0400, C-0403 to C-0405, C-0411,
/// C-0412, C-0421). It is treated as unrecognised on receipt; the contents are kept so it can be
/// re-encoded.
/// </summary>
public sealed record UnrecognisedEow(int EowType, int Contents) : Eow
{
    /// <inheritdoc/>
    public override int Type => EowType;
}

/// <summary>
/// Encodes and decodes the 12-bit EOW [Ed4 C.4.2.2, C.6]: bits 11-8 are the type and bits 7-0 the
/// contents (C-0100, C-0101, C-0422). Reserved bits are written as zero and ignored on receipt (C-0093,
/// C-0094, C-1020).
/// </summary>
public static class EowCodec
{
    /// <summary>Encodes to the 12-bit value, 0 to 4095.</summary>
    /// <exception cref="ArgumentException">A field is outside its range.</exception>
    public static int Encode(Eow eow)
    {
        ArgumentNullException.ThrowIfNull(eow);
        int c = eow switch
        {
            EmptyEow => 0,
            Ed3BasicRateEow e => (R(e.DataRate, 15) << 4) | (R(e.Interleaving, 3) << 2) | R(e.OtherParameters, 3),
            UnrecognizedTypeErrorEow e => R(e.UnrecognizedEowType, 15),
            Ed3CapabilityEow e => (e.DrcCapable ? 0x80 : 0) | (e.Stanag4529 ? 0x40 : 0) | (e.Ms110_75To2400 ? 0x20 : 0)
                | (e.ExtendedRate ? 0x10 : 0) | (e.FullDuplex ? 8 : 0) | (e.SplitFrequency ? 4 : 0)
                | (e.NonArcsAle ? 2 : 0) | (e.Arcs ? 1 : 0),
            MaxSpeedEow e => e.Value.ToByte(),
            LowSpeedEow e => e.Value.ToByte(),
            SpeedUsedEow e => e.Value.ToByte(),
            MaxSegmentEow e => R(e.Value, 255),
            SenderApproachEow e => (e.Eow12Needed ? 8 : 0) | (R(e.Strategy, 3) << 1) | (e.Fixed ? 1 : 0),
            CapabilityEow e => (e.Duplex ? 2 : 0) | (e.Ed4 ? 1 : 0),
            UnrecognisedEow e when e.EowType is 2 or 5 or 6 or 7 or 13 or 14 => R(e.Contents, 255),
            _ => throw new ArgumentException("not an encodable EOW", nameof(eow)),
        };
        return (eow.Type << 8) | c;
    }

    /// <summary>Decodes a 12-bit EOW. Every 12-bit value decodes (C-0094).</summary>
    public static Eow Decode(int value)
    {
        if (value is < 0 or > 4095)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "an EOW is 12 bits");
        }

        int type = value >> 8;
        int c = value & 255;
        return type switch
        {
            0 => new EmptyEow(),
            1 => new Ed3BasicRateEow(c >> 4, (c >> 2) & 3, c & 3),
            3 => new UnrecognizedTypeErrorEow(c & 15),
            4 => new Ed3CapabilityEow((c & 0x80) != 0, (c & 0x40) != 0, (c & 0x20) != 0, (c & 0x10) != 0, (c & 8) != 0, (c & 4) != 0, (c & 2) != 0, (c & 1) != 0),
            8 => new MaxSpeedEow(SpeedInterleaver.FromByte((byte)c)),
            9 => new LowSpeedEow(SpeedInterleaver.FromByte((byte)c)),
            10 => new MaxSegmentEow(c),
            11 => new SenderApproachEow((c & 8) != 0, (c >> 1) & 3, (c & 1) != 0),
            12 => new SpeedUsedEow(SpeedInterleaver.FromByte((byte)c)),
            15 => new CapabilityEow((c & 2) != 0, (c & 1) != 0),
            _ => new UnrecognisedEow(type, c),
        };
    }

    private static int R(int value, int max) =>
        value >= 0 && value <= max ? value : throw new ArgumentException($"value must be 0 to {max}");
}
