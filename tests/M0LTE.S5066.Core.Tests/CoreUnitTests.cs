namespace M0LTE.S5066.Core.Tests;

public class CoreUnitTests
{
    // Matrix rows C-0147 and C-0148: the WARNING D_PDU header, CRC 0x1E5F, on the air 5F 1E.
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
