using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Core.Tests;

/// <summary>
/// The P4-2 ARQ machine (<see cref="ArqLink"/>) on paths the corpus scenarios do not reach:
/// FSN wrap-around, window arithmetic, out-of-order reassembly and the bitmap. The end-to-end
/// behaviour is in the dts-arq corpus scenarios.
/// </summary>
public class ArqLinkTests
{
    private static readonly ReadOnlyMemory<byte> NoData = Array.Empty<byte>();

    [Fact]
    public void The_window_starts_empty_and_counts_the_fsns_in_use()
    {
        var link = new ArqLink(1);
        link.TxLwe.Should().Be(0);       // C-0782
        link.TxUwe.Should().Be(65535);
        link.WindowSize.Should().Be(0);  // C-0785: UWE - (LWE - 1) at the initial values
        link.AllocateFsn().Should().Be(0);
        link.AllocateFsn().Should().Be(1);
        link.AllocateFsn().Should().Be(2);
        link.WindowSize.Should().Be(3);  // C-0770
    }

    [Fact]
    public void Fsns_wrap_modulo_65536()
    {
        var link = new ArqLink(1) { TxLwe = 65535, TxUwe = 65534 };
        link.WindowSize.Should().Be(0);
        link.AllocateFsn().Should().Be(65535);   // C-0190, C-0191
        link.AllocateFsn().Should().Be(0);
        link.WindowSize.Should().Be(2);
    }

    [Fact]
    public void Segments_received_out_of_order_are_joined_in_fsn_order()
    {
        var link = new ArqLink(1);
        link.Receive(2, false, true, false, false, [3, 4]).Should().BeEmpty();
        link.Receive(0, true, false, false, false, [1]).Should().BeEmpty();
        link.RxLwe.Should().Be(1);   // C-0776
        link.RxUwe.Should().Be(2);   // C-0778
        var got = link.Receive(1, false, false, false, false, [2]);
        got.Should().ContainSingle().Which.ToArray().Should().Equal(1, 2, 3, 4);   // C-0516
        link.RxLwe.Should().Be(3);
    }

    [Fact]
    public void The_bitmap_covers_rx_lwe_plus_one_to_rx_uwe()
    {
        var link = new ArqLink(1);
        link.Receive(2, true, true, false, false, [9]);
        link.Receive(4, true, true, false, false, [9]);
        // Bits for FSNs 1 to 4: 2 is bit 1, 4 is bit 3 (C-0207, C-0208, C-0210).
        link.AckBitmap().Should().Equal(0x0A);
        link.Receive(0, true, true, false, false, [9]);
        link.Receive(1, true, true, false, false, [9]);
        link.RxLwe.Should().Be(3);
        link.AckBitmap().Should().Equal(0x01);   // FSN 4 only
    }

    [Fact]
    public void The_acknowledgement_takes_the_more_compact_encoding_and_the_bitmap_on_a_tie()
    {
        // C-0225, C-1150
        var link = new ArqLink(1);
        link.Acknowledgement().Should().ContainSingle().Which.Data.Should().BeEmpty();   // nothing beyond RX LWE
        link.Receive(2, true, true, false, false, [9]);
        AckUnit tie = link.Acknowledgement().Should().ContainSingle().Subject;   // bits 0 1: bitmap 1 byte, RLE 2 bytes
        (tie.RxLwe, tie.Encoding, tie.Offset).Should().Be((0, 0, 0));
        tie.Data.Should().Equal(0x02);
        for (int f = 3; f <= 20; f++)
        {
            link.Receive(f, false, false, false, false, [9]);
        }

        // FSN 1 missing, 2 to 20 held: bits 0 then nineteen 1s. Bitmap 3 bytes, RLE 2 bytes.
        AckUnit rle = link.Acknowledgement().Single();
        rle.Encoding.Should().Be(1);
        rle.Data.Should().Equal(0x00, 0x92);
    }

    [Fact]
    public void The_acknowledgement_is_split_by_offset_when_it_does_not_fit()
    {
        // C-0222, C-0223, C-0233, C-1151: FSN 3 and every third FSN to 3000; no single field of 255 bytes holds it.
        var link = new ArqLink(1);
        for (int f = 3; f <= 3000; f += 3)
        {
            link.Receive(f, false, false, false, false, [9]);
        }

        IReadOnlyList<AckUnit> units = link.Acknowledgement();
        units.Select(u => u.Offset).Should().Equal(0, 7);
        units.Should().OnlyContain(u => u.RxLwe == 0 && u.Encoding == 0 && u.Data.Length <= ArqLink.MaxAckBytes);
        units[0].Data.Length.Should().Be(224);                  // seven 256-FSN units as a bitmap
        units[1].Data.Length.Should().Be((3000 - 1792 + 7) / 8);  // the rest, FSN 1793 to 3000
        var bits = units.SelectMany(u => AckData.Decode(u.Encoding, u.Data).Take(u == units[0] ? 1792 : 3000 - 1792)).ToList();
        bits.Should().HaveCount(3000);
        bits.Select((b, i) => (b, i)).Should().OnlyContain(x => x.b == ((x.i + 2) % 3 == 1));
    }

    [Fact]
    public void A_copy_behind_rx_lwe_owes_an_acknowledgement_but_is_not_delivered_again()
    {
        var link = new ArqLink(1);
        link.Receive(0, true, true, false, false, [7]).Should().ContainSingle();
        link.AckOwed = false;
        link.Receive(0, true, true, false, false, [7]).Should().BeEmpty();   // C-0777
        link.AckOwed.Should().BeTrue();                                        // C-1126
        link.RxLwe.Should().Be(1);
    }

    [Fact]
    public void A_drop_pdu_discards_its_cpdu_whichever_segment_carries_it()
    {
        var link = new ArqLink(1);
        link.Receive(0, true, false, false, true, NoData.Span).Should().BeEmpty();
        link.Receive(1, false, true, false, false, [5]).Should().BeEmpty();   // C-0184, C-1125
        link.RxLwe.Should().Be(2);
    }

    [Fact]
    public void An_in_order_cpdu_is_released_when_rx_lwe_passes_it_though_a_later_one_still_waits()
    {
        // A (FSN 1) and C (FSN 4) are in-order C_PDUs. FSN 0 and FSN 3 are missing, so both wait.
        // When FSN 0 and then FSN 2 arrive RX LWE reaches 3: A may go (C-1123) while C, still
        // ahead of RX LWE, waits for FSN 3.
        var link = new ArqLink(1);
        link.Receive(1, true, true, true, false, [0xA]).Should().BeEmpty();
        link.Receive(4, true, true, true, false, [0xC]).Should().BeEmpty();
        link.Receive(2, true, true, false, false, [0xB]).Should().ContainSingle();   // no flag: at once (C-0179)
        link.Receive(0, true, true, false, false, [0x0]).Select(m => m.ToArray()[0]).Should().Equal(0x0, 0xA);
        link.RxLwe.Should().Be(3);
        link.Receive(3, true, true, false, false, [0xD]).Select(m => m.ToArray()[0]).Should().Equal(0xD, 0xC);
    }
}
