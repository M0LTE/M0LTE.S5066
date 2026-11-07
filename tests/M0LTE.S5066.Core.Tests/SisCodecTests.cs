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

    [Fact]
    public void Decoder_results_do_not_depend_on_chunking()
    {
        // Random mixes of good frames, junk, bad headers and wrong sizes (corpus-format.md section 3.5, S-0159).
        var rng = new Random(5066);
        byte[][] pieces =
        [
            SisCodec.Encode(new SisKeepAlive()),
            SisCodec.Encode(new SisUnidataRequest(12, 5, SisAddress.FromParts(0, 0, 0, 5), ServiceType.Default, 0, new byte[] { 0x90, 0xEB, 0x00 })),
            [0x90, 0xEB, 0x01, 0x00, 0x01, 0x11],
            [0x90, 0xEB, 0x00, 0x00, 0x00],
            [0x90, 0xEB, 0x00, 0x00, 0x02, 0x07, 0x90],
            [0x90, 0xEB, 0x00, 0x00, 0x02, 0x11, 0xEB],
            [0x90],
            [0xEB],
            [0x00, 0x90, 0x90],
        ];

        for (int trial = 0; trial < 500; trial++)
        {
            var bytes = new List<byte>();
            for (int i = rng.Next(1, 8); i > 0; i--)
            {
                bytes.AddRange(pieces[rng.Next(pieces.Length)]);
            }

            byte[] all = [.. bytes];
            List<string> whole = Run(all, [all.Length]);
            Run(all, [.. Enumerable.Repeat(1, all.Length)]).Should().Equal(whole, $"trial {trial}, byte at a time");
            var cuts = new List<int>();
            for (int left = all.Length; left > 0;)
            {
                int n = Math.Min(left, rng.Next(0, 6));
                cuts.Add(n);
                left -= n;
            }

            Run(all, cuts).Should().Equal(whole, $"trial {trial}, random chunks");
        }

        static List<string> Run(byte[] all, IEnumerable<int> sizes)
        {
            var d = new SisFrameDecoder();
            var got = new List<string>();
            int at = 0;
            foreach (int n in sizes)
            {
                d.Push(all.AsSpan(at, n));
                at += n;
                while (d.TryRead(out SisDecodeResult r))
                {
                    got.Add(r.Primitive is { } p ? SisCorpus.ToJson(p).ToJsonString() : r.Error.ToString()!);
                }
            }

            return got;
        }
    }
}
