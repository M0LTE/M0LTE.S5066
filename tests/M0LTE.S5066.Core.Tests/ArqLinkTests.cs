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
        // C-0213, C-0222, C-0233, C-1151: FSN 3 and every third FSN to 3000. Each 256-FSN unit needs a
        // 32-byte bitmap (its run length encoding is longer), so each ACK-ONLY covers one unit.
        var link = new ArqLink(1);
        for (int f = 3; f <= 3000; f += 3)
        {
            link.Receive(f, false, false, false, false, [9]);
        }

        IReadOnlyList<AckUnit> units = link.Acknowledgement();
        units.Select(u => u.Offset).Should().Equal(Enumerable.Range(0, 12));
        units.Should().OnlyContain(u => u.RxLwe == 0 && u.Encoding == 0 && u.Data.Length <= ArqLink.MaxAckBytes);
        units.Take(11).Should().OnlyContain(u => u.Data.Length == 32);   // one 256-FSN unit as a bitmap
        units[11].Data.Length.Should().Be((3000 - 2816 + 7) / 8);       // the rest, FSN 2817 to 3000
        var bits = units.SelectMany(u => AckData.Decode(u.Encoding, u.Data).Take(u.Offset < 11 ? 256 : 3000 - 2816)).ToList();
        bits.Should().HaveCount(3000);
        bits.Select((b, i) => (b, i)).Should().OnlyContain(x => x.b == ((x.i + 2) % 3 == 1));
    }

    [Fact]
    public void A_compact_run_length_encoding_covers_several_offset_units_in_one_ack_only()
    {
        // C-1150, C-1151: FSN 1 to 1000 held, then every second FSN to 1299. Four units (FSN 1 to 1024)
        // run length encode in 25 bytes (a two-byte code for the run FSN 1 to 1001, then 23 one-byte
        // codes); a fifth would not fit 32 bytes. The rest, 275 bits, needs a 35-byte bitmap, so it is split again at Offset 5.
        var link = new ArqLink(1);
        for (int f = 1; f <= 1000; f++)
        {
            link.Receive(f, false, false, false, false, [9]);
        }

        for (int f = 1001; f <= 1300; f += 2)
        {
            link.Receive(f, false, false, false, false, [9]);
        }

        IReadOnlyList<AckUnit> units = link.Acknowledgement();
        units.Select(u => (u.Offset, u.Encoding, u.Data.Length)).Should().Equal((0, 1, 25), (4, 0, 32), (5, 0, 3));
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

    [Fact]
    public void Equations_1_to_3_are_arcs_from_the_receive_edges()
    {
        var link = new ArqLink(1);
        // Initial edges (C-0786): Equation 1 accepts 65535 forward to 32767, Equation 2 32768 forward to 0.
        link.InSyncAsDestination(0, true, true).Should().BeTrue();
        link.InSyncAsDestination(32767, true, false).Should().BeTrue();
        link.InSyncAsDestination(32768, true, false).Should().BeFalse();   // C-0794
        link.InSyncAsDestination(1, false, true).Should().BeFalse();       // C-0797: TX LWE beyond RX LWE
        link.InSyncAsDestination(40000, false, false).Should().BeTrue();   // Equation 3 holds every FSN while nothing is held

        link.Receive(0, true, true, false, false, NoData.Span);
        link.Receive(2, true, true, false, false, NoData.Span);   // RX LWE 1, RX UWE 2
        link.InSyncAsDestination(32768, false, false).Should().BeTrue();
        link.InSyncAsDestination(32769, false, false).Should().BeFalse();  // C-0798: the gap Equation 3 leaves
        link.InSyncAsDestination(32770, false, false).Should().BeFalse();
        link.InSyncAsDestination(32771, false, false).Should().BeTrue();   // a copy behind RX LWE (C-1126)
        link.InSyncAsDestination(0, true, false).Should().BeFalse();       // TX UWE behind RX UWE
    }

    [Fact]
    public void Equations_4_and_5_are_arcs_from_the_transmit_edges()
    {
        var link = new ArqLink(1);
        link.InSyncAsOriginator(0, []).Should().BeTrue();
        link.InSyncAsOriginator(1, []).Should().BeFalse();       // C-0800: beyond TX UWE + 1 = 0
        link.InSyncAsOriginator(0, [1]).Should().BeFalse();      // C-0786: nothing outstanding
        link.AllocateFsn();
        link.AllocateFsn();
        link.AllocateFsn();                                      // FSNs 0 to 2 outstanding
        link.InSyncAsOriginator(3, []).Should().BeTrue();
        link.InSyncAsOriginator(0, [2]).Should().BeTrue();
        link.InSyncAsOriginator(0, [0]).Should().BeFalse();      // C-0801: TX LWE itself is excluded
        link.InSyncAsOriginator(0, [3]).Should().BeFalse();      // beyond TX UWE
        link.InSyncAsOriginator(65535, []).Should().BeFalse();   // behind TX LWE
    }

    [Fact]
    public void A_receive_resync_keeps_what_lies_in_the_new_window_and_releases_in_order_data()
    {
        var link = new ArqLink(1);
        link.Receive(2, true, true, true, false, new byte[] { 2 }).Should().BeEmpty();   // in order: waits for 0 and 1
        link.Receive(5, true, false, false, false, new byte[] { 5 }).Should().BeEmpty(); // a C_PDU start
        var got = link.ResyncReceive(3);   // C-1178: the peer commands RX LWE 3, its TX LWE
        got.Should().BeEmpty("FSN 2 is behind the new RX LWE and is discarded");
        link.RxLwe.Should().Be(3);
        link.RxUwe.Should().Be(5);
        link.Receive(6, false, true, false, false, new byte[] { 6 }).Single().ToArray().Should().Equal(5, 6);

        var other = new ArqLink(1);
        other.Receive(1, true, true, true, false, new byte[] { 1 }).Should().BeEmpty();
        other.ResyncReceive(1).Single().ToArray().Should().Equal(1);   // RX LWE passes FSN 1, so it goes
        other.RxLwe.Should().Be(2);
    }

    [Fact]
    public void A_receive_resync_never_moves_rx_lwe_back_over_delivered_data()
    {
        var link = new ArqLink(1);
        for (int f = 0; f < 10; f++)
        {
            link.Receive(f, true, true, false, false, new byte[] { (byte)f }).Should().ContainSingle();
        }

        // The peer's TX LWE 3 lags our RX LWE 10 (its acknowledgements were lost): that passes
        // Equation 2, so nothing moves, and its retransmissions of FSNs 3 to 9 stay copies (C-1178).
        link.ResyncReceive(3).Should().BeEmpty();
        link.RxLwe.Should().Be(10);
        link.RxUwe.Should().Be(9);
        link.Receive(5, true, true, false, false, new byte[] { 5 }).Should().BeEmpty("FSN 5 was delivered already");
    }

    [Fact]
    public void A_full_reset_returns_to_the_initial_edges()
    {
        var link = new ArqLink(1);
        link.AllocateFsn();
        link.Receive(0, true, false, false, false, new byte[] { 1 });
        link.Reset();   // C-0812, C-0814
        link.TxLwe.Should().Be(0);
        link.TxUwe.Should().Be(65535);
        link.RxLwe.Should().Be(0);
        link.RxUwe.Should().Be(65535);
        link.AckOwed.Should().BeFalse();
        link.Receive(0, true, true, false, false, new byte[] { 9 }).Single().ToArray().Should().Equal(9);
    }
}

