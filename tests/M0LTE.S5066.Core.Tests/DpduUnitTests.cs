using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Tests;

/// <summary>Behaviour of the D_PDU codecs that the corpus cannot express (encode errors, stepping, helpers).</summary>
public class DpduUnitTests
{
    private static readonly DpduHeader Hdr = new(0, 0, 2, 5, 100);

    [Fact]
    public void Every_12_bit_eow_decodes_and_reencodes_to_itself_when_reserved_bits_are_zero()
    {
        for (int v = 0; v < 4096; v++)
        {
            Eow e = EowCodec.Decode(v);
            e.Type.Should().Be(v >> 8);
            // Reserved bits are ignored on receipt (C-1020), so the value may differ only in them.
            int again = EowCodec.Encode(e);
            EowCodec.Decode(again).Should().Be(e);
        }
    }

    [Fact]
    public void DecodeFirst_steps_through_back_to_back_pdus()
    {
        byte[] a = DpduCodec.Encode(new Warning(Hdr, 0, 2));
        byte[] b = DpduCodec.Encode(new Padding(Hdr));
        byte[] both = [.. a, .. b];
        DecodeResult<Dpdu> first = DpduCodec.DecodeFirst(both, PeerEdition.Ed4);
        first.Consumed.Should().Be(a.Length);
        first.Value.Should().BeOfType<Warning>();
        DpduCodec.DecodeFirst(both.AsSpan(first.Consumed), PeerEdition.Ed4).Value.Should().BeOfType<Padding>();
        DpduCodec.Decode(both, PeerEdition.Ed4).Error.Should().Be(PduErrorKind.TrailingBytes);
    }

    [Fact]
    public void An_extension_is_unrecognised_and_consumes_only_its_header()
    {
        byte[] ext = DpduCodec.Encode(new Extension(Hdr, 2, new Octets([0xA5])));
        byte[] following = [.. ext, 0x00, 0x11, 0x90, 0xEB];
        DecodeResult<Dpdu> r = DpduCodec.DecodeFirst(following, PeerEdition.Ed4);
        r.Consumed.Should().Be(ext.Length, "the sync search resumes after the header CRC (C-0365)");
        ((Extension)r.Value!).IsRecognised.Should().BeFalse();
        DpduCodec.Decode(following, PeerEdition.Ed4).IsSuccess.Should().BeTrue("bytes after an extension header are not trailing bytes");
    }

    [Fact]
    public void Address_read_rejects_sizes_outside_1_to_7()
    {
        DpduAddress.TryRead([], out _, out _).Should().BeFalse();
        DpduAddress.TryRead(new byte[8], out _, out _).Should().BeFalse();
        DpduAddress.TryRead([0x5A], out uint dst, out uint src).Should().BeTrue();
        (dst, src).Should().Be((5u, 10u));
    }

    [Fact]
    public void Encoder_rejects_out_of_range_fields()
    {
        var seg = new Octets(new byte[1024]);
        var flags = default(DataFlags);
        ((Action)(() => DpduCodec.Encode(new DataOnly(Hdr, flags, 0, seg, true)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new Ed3DataOnly(Hdr, flags, 256, Octets.Empty, true)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new DataOnly(Hdr, flags, 65536, Octets.Empty, true)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new Ed3AckOnly(Hdr, 0, new Octets(new byte[17]))))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new DataAck(Hdr, flags, 0, 0, 0, new Octets(new byte[19]), Octets.Empty, true)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new Warning(Hdr with { Eow = 4096 }, 0, 0)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new Warning(Hdr with { Eot = 256 }, 0, 0)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new Warning(Hdr with { AddressSize = 0 }, 0, 0)))).Should().Throw<ArgumentException>();
        ((Action)(() => DpduCodec.Encode(new Warning(Hdr with { AddressSize = 1 }, 0, 0)))).Should().Throw<ArgumentException>("100 does not fit one nibble");
        ((Action)(() => DpduCodec.Encode(new Management(Hdr, false, true, false, 0, new Octets(new byte[24]))))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encoder_writes_correct_crcs_even_when_the_value_says_they_were_bad()
    {
        byte[] bytes = DpduCodec.Encode(new NonArqData(Hdr, new NonArqFields(1, false, false, 3, 0, 9), new Octets([1, 2, 3]), false));
        ((NonArqData)DpduCodec.Decode(bytes, PeerEdition.Any).Value!).SegmentCrcOk.Should().BeTrue();
    }

    [Theory]
    [InlineData(0u, 0u, 1)]
    [InlineData(15u, 15u, 1)]
    [InlineData(16u, 0u, 2)]
    [InlineData(5u, 100u, 2)]
    [InlineData(0x0FFFu, 0u, 3)]
    [InlineData(0x1000u, 0u, 4)]
    [InlineData(0x0FFFFFFFu, 0u, 7)]
    public void Minimum_address_size_is_the_nibbles_needed(uint dst, uint src, int expected) =>
        DpduAddress.MinimumSize(dst, src).Should().Be(expected);

    [Theory]
    [InlineData("0.0.0.0", true, 0u)]
    [InlineData("15.255.255.255", true, 0x0FFFFFFFu)]
    [InlineData("16.0.0.0", false, 0u)]
    [InlineData("0.0.0.256", false, 0u)]
    [InlineData("1.2.3", false, 0u)]
    [InlineData("a.b.c.d", false, 0u)]
    public void Dotted_addresses_parse_and_format(string text, bool ok, uint value)
    {
        DpduAddress.TryParse(text, out uint v).Should().Be(ok);
        if (ok)
        {
            v.Should().Be(value);
            DpduAddress.ToDotted(v).Should().Be(text);
        }
    }

    [Fact]
    public void Undefined_types_cannot_be_encoded() =>
        ((Action)(() => DpduCodec.Encode(new UndefinedTypeDpdu(Hdr, 9, Octets.Empty)))).Should().Throw<ArgumentException>();
}
