namespace M0LTE.S5066.Core.Tests;

public class CoreUnitTests
{
    // Matrix row C-0147: the WARNING D_PDU header, CRC 0x1E5F, on the air 5F 1E (C-0148).
    private static readonly byte[] WarningHeader = [0xF0, 0x00, 0x00, 0x47, 0x05, 0x64, 0x02];

    [Fact]
    public void Crc16_matches_the_annex_c_worked_example()
    {
        ushort crc = Crc16S5066.Compute(WarningHeader);
        crc.Should().Be(0x1E5F);

        Span<byte> wire = stackalloc byte[2];
        Crc16S5066.Write(crc, wire);
        wire.ToArray().Should().Equal(0x5F, 0x1E);
        Crc16S5066.Read(wire).Should().Be(crc);
    }

    [Fact]
    public void Crc16_of_nothing_is_the_initial_register()
    {
        Crc16S5066.Compute([]).Should().Be(Crc16S5066.Initial);
    }

    // Matrix row C-0164.
    [Fact]
    public void Crc32_matches_the_annex_c_test_vector()
    {
        uint crc = Crc32S5066.Compute(WarningHeader);
        crc.Should().Be(0xF4178F95u);

        Span<byte> wire = stackalloc byte[4];
        Crc32S5066.Write(crc, wire);
        wire.ToArray().Should().Equal(0x95, 0x8F, 0x17, 0xF4);
        Crc32S5066.Read(wire).Should().Be(crc);
    }

    [Fact]
    public void Crc_table_agrees_with_the_bitwise_reference_algorithm()
    {
        // The bit-at-a-time loops are Code Example C-1 and C-2 (C-0143, C-0162) transcribed.
        var rng = new SplitMix64(1);
        for (int trial = 0; trial < 50; trial++)
        {
            var data = new byte[(int)rng.Uniform(40)];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)rng.NextUInt64();
            }

            ushort c16 = 0;
            uint c32 = 0;
            foreach (byte b in data)
            {
                for (int i = 0; i < 8; i++)
                {
                    bool bit = ((c16 & 1) != 0) ^ ((b >> i & 1) != 0);
                    c16 >>= 1;
                    if (bit)
                    {
                        c16 ^= 0x9299;
                    }

                    bit = ((c32 & 1) != 0) ^ ((b >> i & 1) != 0);
                    c32 >>= 1;
                    if (bit)
                    {
                        c32 ^= 0xF3A4E550;
                    }
                }
            }

            Crc16S5066.Compute(data).Should().Be(c16);
            Crc32S5066.Compute(data).Should().Be(c32);
        }
    }

    // An independent check that uses neither 0x9299 nor 0xF3A4E550: the polynomial is built
    // from its exponents (C-0138, C-0158), the message is taken as a bit sequence in on-air order
    // (each byte LSB first), and the remainder of M(x) x^w divided by P(x) is found by plain
    // long division over GF(2). The first bit on the air is the highest-order coefficient of
    // the remainder (C-0142), which is bit 0 of the register, and the register goes out low byte
    // first (C-0145, C-0163); so the remainder read in order is the CRC bytes as sent.
    [Theory]
    [InlineData(16, new[] { 16, 15, 12, 11, 8, 6, 3, 0 })]
    [InlineData(32, new[] { 32, 27, 25, 23, 21, 18, 17, 16, 13, 10, 8, 7, 6, 3, 2, 1, 0 })]
    public void Crc_matches_gf2_long_division_by_the_stated_polynomial(int width, int[] exponents)
    {
        var rng = new SplitMix64(0x5066);
        List<byte[]> messages = [WarningHeader, [], [0x00], [0xFF], [0x01, 0x80]];
        for (int trial = 0; trial < 40; trial++)
        {
            var data = new byte[(int)rng.Uniform(64)];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)rng.NextUInt64();
            }

            messages.Add(data);
        }

        foreach (byte[] data in messages)
        {
            bool[] remainder = Gf2Remainder(data, width, exponents);
            var expectedWire = new byte[width / 8];
            for (int k = 0; k < width; k++)
            {
                if (remainder[k])
                {
                    expectedWire[k / 8] |= (byte)(1 << (k % 8));
                }
            }

            var wire = new byte[width / 8];
            if (width == 16)
            {
                Crc16S5066.Write(Crc16S5066.Compute(data), wire);
            }
            else
            {
                Crc32S5066.Write(Crc32S5066.Compute(data), wire);
            }

            wire.Should().Equal(expectedWire, $"CRC-{width} of {Convert.ToHexString(data)}");
        }
    }

    // A receiver that runs the CRC over the data followed by the CRC bytes as sent gets zero,
    // which confirms the wire byte order matches the register orientation.
    [Fact]
    public void Crc_over_data_and_its_transmitted_crc_is_zero()
    {
        var with16 = new byte[WarningHeader.Length + 2];
        WarningHeader.CopyTo(with16, 0);
        Crc16S5066.Write(Crc16S5066.Compute(WarningHeader), with16.AsSpan(WarningHeader.Length));
        Crc16S5066.Compute(with16).Should().Be(0);

        var with32 = new byte[WarningHeader.Length + 4];
        WarningHeader.CopyTo(with32, 0);
        Crc32S5066.Write(Crc32S5066.Compute(WarningHeader), with32.AsSpan(WarningHeader.Length));
        Crc32S5066.Compute(with32).Should().Be(0);
    }

    private static bool[] Gf2Remainder(byte[] data, int width, int[] exponents)
    {
        // Dividend: the message bits in on-air order followed by width zero bits.
        var bits = new bool[data.Length * 8 + width];
        for (int i = 0; i < data.Length * 8; i++)
        {
            bits[i] = ((data[i / 8] >> (i % 8)) & 1) != 0;
        }

        // Divisor coefficients, highest degree first.
        var divisor = new bool[width + 1];
        foreach (int e in exponents)
        {
            divisor[width - e] = true;
        }

        for (int i = 0; i < data.Length * 8; i++)
        {
            if (bits[i])
            {
                for (int j = 0; j <= width; j++)
                {
                    bits[i + j] ^= divisor[j];
                }
            }
        }

        return bits[(data.Length * 8)..];
    }

    [Fact]
    public void Uniform_of_one_still_draws_once_and_returns_zero()
    {
        var rng = new SplitMix64(0);
        rng.Uniform(1).Should().Be(0);
        rng.Draws.Should().Be(1);
    }

    [Fact]
    public void Uniform_of_zero_is_rejected()
    {
        var rng = new SplitMix64(0);
        Action act = () => rng.Uniform(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
