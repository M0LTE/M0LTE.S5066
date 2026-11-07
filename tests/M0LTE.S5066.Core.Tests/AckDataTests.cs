using M0LTE.S5066.Core.Dts;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The two Ack Data encodings of [Ed4 C.4.5.5, pp.C-31 to C-32] (<see cref="AckData"/>): the bitmap
/// and run length encoding, the code forms of Figures C-15 and C-16, and decoding.
/// </summary>
public class AckDataTests
{
    private static List<bool> Bits(string s) => s.Select(c => c == '1').ToList();

    [Fact]
    public void The_bitmap_fills_each_byte_from_bit_0_and_pads_with_zero()
    {
        // C-0207, C-0208, C-0210, C-0227
        AckData.Bitmap(Bits("1010000011"), 0, 10).Should().Equal(0x05, 0x03);
        AckData.Bitmap(Bits(""), 0, 0).Should().BeEmpty();
    }

    [Fact]
    public void Runs_of_up_to_64_bits_take_one_byte_and_longer_runs_two()
    {
        // C-0229 to C-0231, C-1150: Length is the repeat count less one.
        AckData.Rle(Bits("1"), 0, 1).Should().Equal(0x80);
        AckData.Rle(Bits("000"), 0, 3).Should().Equal(0x02);
        AckData.Rle(Enumerable.Repeat(true, 64).ToList(), 0, 64).Should().Equal(0xBF);
        AckData.Rle(Enumerable.Repeat(true, 65).ToList(), 0, 65).Should().Equal(0xC0, 0x40);
        AckData.Rle(Enumerable.Repeat(false, 16384).ToList(), 0, 16384).Should().Equal(0x7F, 0xFF);
    }

    [Fact]
    public void A_run_longer_than_16384_bits_takes_full_codes_then_one_for_the_rest()
    {
        // C-1150: 16384 + 64 ones = one full two-byte code, then a one-byte code of 64.
        AckData.Rle(Enumerable.Repeat(true, 16448).ToList(), 0, 16448).Should().Equal(0xFF, 0xFF, 0xBF);
    }

    [Fact]
    public void Codes_follow_one_another_and_decode_back()
    {
        // C-0232, C-0226
        List<bool> bits = Bits(new string('1', 70) + "000" + "1");
        byte[] rle = AckData.Rle(bits, 0, bits.Count);
        rle.Should().Equal(0xC0, 0x45, 0x02, 0x80);
        AckData.Decode(AckData.RleEncoding, rle).Should().Equal(bits);
        AckData.Decode(AckData.BitmapEncoding, AckData.Bitmap(bits, 0, bits.Count)).Take(bits.Count).Should().Equal(bits);
    }

    [Fact]
    public void A_two_byte_code_cut_short_by_the_end_of_the_field_adds_nothing()
    {
        // C-1153
        AckData.Decode(AckData.RleEncoding, [0x81, 0xC0]).Should().Equal(true, true);
    }

    [Fact]
    public void A_range_inside_the_bits_can_be_encoded()
    {
        List<bool> bits = Bits("0000" + "1100");
        AckData.Bitmap(bits, 4, 4).Should().Equal(0x03);
        AckData.Rle(bits, 4, 4).Should().Equal(0x81, 0x01);
    }
}
