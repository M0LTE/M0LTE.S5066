using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Core.Tests;

public class SisCodecTests
{
    [Fact]
    public void Encode_rejects_out_of_range_fields()
    {
        var addr = SisAddress.FromParts(0, 0, 0, 5);
        Action sap = () => SisCodec.Encode(new SisBindRequest(16, ServiceType.Default));
        Action prio = () => SisCodec.Encode(new SisUnidataRequest(16, 0, addr, ServiceType.Default, 0, ReadOnlyMemory<byte>.Empty));
        Action ttl = () => SisCodec.Encode(new SisUnidataRequest(0, 0, addr, ServiceType.Default, 0x100000, ReadOnlyMemory<byte>.Empty));
        Action huge = () => SisCodec.Encode(new SisManagementMsgRequest(1, new byte[65534]));
        sap.Should().Throw<ArgumentException>();
        prio.Should().Throw<ArgumentException>();
        ttl.Should().Throw<ArgumentException>();
        huge.Should().Throw<ArgumentException>();
        SisCodec.Encode(new SisManagementMsgRequest(1, new byte[65533])).Length.Should().Be(5 + 65535);
    }

    [Fact]
    public void Address_parses_and_prints_dotted_text()
    {
        SisAddress.TryParse("15.255.255.255", true, 7, out var a).Should().BeTrue();
        a.Value.Should().Be(SisAddress.MaxValue);
        a.Dotted.Should().Be("15.255.255.255");
        SisAddress.TryParse("16.0.0.0", false, 7, out _).Should().BeFalse();
        SisAddress.TryParse("1.2.3", false, 7, out _).Should().BeFalse();
    }

    [Fact]
    public void Decoder_empties_its_buffer_when_drained()
    {
        var d = new SisFrameDecoder();
        d.Push(SisCodec.Encode(new SisKeepAlive()));
        d.TryRead(out var r).Should().BeTrue();
        r.Primitive.Should().BeOfType<SisKeepAlive>();
        d.TryRead(out _).Should().BeFalse();
        d.BufferedBytes.Should().Be(0);
    }

    [Fact]
    public void Decoder_holds_junk_no_longer_than_one_byte()
    {
        var d = new SisFrameDecoder();
        d.Push(new byte[1000]);
        d.TryRead(out var r).Should().BeTrue();
        r.Error.Should().Be(SisErrorKind.BadSync);
        d.TryRead(out _).Should().BeFalse();
        d.BufferedBytes.Should().Be(0);
        d.Push(new byte[] { 0x90 });
        d.TryRead(out _).Should().BeFalse();
        d.BufferedBytes.Should().Be(1);
    }

    [Fact]
    public void Deprecated_and_unknown_results_carry_the_type_byte()
    {
        SisCodec.DecodeFrame([0x90, 0xEB, 0, 0, 1, 24]).TypeByte.Should().Be(24);
        SisCodec.DecodeFrame([0x90, 0xEB, 0, 0, 1, 99]).TypeByte.Should().Be(99);
    }
}
