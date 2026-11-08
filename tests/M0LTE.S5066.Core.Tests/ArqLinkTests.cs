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
    public void The_8_bit_family_has_modulus_256_and_a_window_of_128()
    {
        // C-0190, C-0558, C-0766, C-0771, C-0782.
        var link = new ArqLink(1, shortFamily: true);
        (link.Seq.Modulus, link.MaxWindowSize, link.TxLwe, link.TxUwe, link.RxLwe, link.RxUwe).Should().Be((256, 128, 0, 255, 0, 255));
        link.WindowSize.Should().Be(0);
        link.TxLwe = 255;
        link.TxUwe = 254;
        link.AllocateFsn().Should().Be(255);
        link.AllocateFsn().Should().Be(0);
        link.WindowSize.Should().Be(2);
        link.Reset();
        (link.TxLwe, link.TxUwe, link.RxUwe).Should().Be((0, 255, 255));   // C-0812, C-0822
    }

    [Fact]
    public void An_8_bit_fsn_128_ahead_of_rx_lwe_is_not_accepted()
    {
        // C-0779: FSN 127 is the furthest a sender may be ahead of RX LWE 0; FSN 128 is outside the window.
        var link = new ArqLink(1, shortFamily: true);
        link.Receive(128, true, true, false, false, [1]).Should().BeEmpty();
        (link.RxLwe, link.RxUwe).Should().Be((0, 255));
        link.Receive(127, true, true, false, false, [1]).Should().ContainSingle();
        link.RxUwe.Should().Be(127);
        link.InSyncAsDestination(127, true, false).Should().BeTrue();    // Equation 1 with MAX WIN SIZE 128 (C-0794)
        link.InSyncAsDestination(200, false, false).Should().BeFalse();  // Equation 3 (C-0798)
    }

    [Fact]
    public void The_8_bit_acknowledgement_is_one_bitmap_of_the_whole_window()
    {
        // C-0194, C-0201, C-0207 to C-0211, interp C-1190: FSNs 1 and 127 held, RX LWE 0; 127 bits, 16 bytes,
        // one unit at offset 0 with no run length choice.
        var link = new ArqLink(1, shortFamily: true);
        link.Receive(1, true, true, false, false, [1]);
        link.Receive(127, true, true, false, false, [1]);
        AckUnit u = link.Acknowledgement().Should().ContainSingle().Which;
        (u.RxLwe, u.Encoding, u.Offset, u.Data.Length).Should().Be((0, 0, 0, 16));
        u.Data[0].Should().Be(0x01);
        u.Data[15].Should().Be(0x40);
        u.Data.Skip(1).Take(14).Should().OnlyContain(b => b == 0);
        link.DataAckLimit.Should().Be(16);
    }

    [Theory]
    [InlineData(130)]
    [InlineData(300)]
    public void An_8_bit_cpdu_longer_than_the_window_or_the_modulus_is_reassembled(int segments)
    {
        // C-0510, C-0512, C-0516: nothing bounds the segments of a C_PDU by the window, and its FSNs
        // wrap modulo 256 (C-0190). Sent in window-sized bursts as a sender must (C-0771), with a
        // late segment arriving out of order in each.
        var link = new ArqLink(1, shortFamily: true);
        var got = new List<ReadOnlyMemory<byte>>();
        for (int b = 0; b < segments; b += 100)
        {
            int end = Math.Min(segments, b + 100);
            for (int i = end - 1; i >= b; i--)
            {
                got.AddRange(link.Receive(i % 256, i == 0, i == segments - 1, true, false, [(byte)i, (byte)(i >> 8)]));
            }

            link.RxLwe.Should().Be(end % 256);
        }

        got.Should().ContainSingle();
        byte[] expected = Enumerable.Range(0, segments).SelectMany(i => new[] { (byte)i, (byte)(i >> 8) }).ToArray();
        got[0].ToArray().Should().Equal(expected);
        link.AckBits().Should().BeEmpty();
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
    public void A_complete_in_order_cpdu_held_behind_a_gap_is_reported_once()
    {
        // Interp B-0460: FSN 0 is missing. B (FSN 1 and 2) is reported as held only when its last
        // segment arrives, a copy of a segment reports nothing, and B is delivered, not reported,
        // when FSN 0 fills the gap. A C_PDU delivered at once (no flag, or nothing missing) is not held.
        var link = new ArqLink(1);
        link.Receive(1, true, false, true, false, [0xB, 0x1]).Should().BeEmpty();
        link.LastHeld.Should().BeNull("B is not complete");
        link.Receive(2, false, true, true, false, [0xB, 0x2]).Should().BeEmpty();
        link.LastHeld!.Value.ToArray().Should().Equal(0xB, 0x1, 0xB, 0x2);
        link.Receive(2, false, true, true, false, [0xB, 0x2]).Should().BeEmpty();
        link.LastHeld.Should().BeNull("a copy completes nothing new");
        link.Receive(4, true, true, false, false, [0xD]).Should().ContainSingle();   // no flag: at once (C-0179)
        link.LastHeld.Should().BeNull();
        link.Receive(0, true, true, true, false, [0xA]).Select(m => m.ToArray()[0]).Should().Equal(0xA, 0xB);
        link.LastHeld.Should().BeNull("delivered, not held");
        link.Receive(3, true, true, true, false, [0xC]).Should().ContainSingle();   // nothing missing before it
        link.LastHeld.Should().BeNull();
    }

    [Fact]
    public void Held_in_order_cpdus_are_released_in_fsn_order_when_the_connection_ends()
    {
        // Interp C-1254: FSN 0 never came, so C_PDUs B (FSN 1) and C (FSN 3 and 4) wait for in-order
        // delivery; D (FSN 6, 7) is partly received. When the connection ends B and C go up, in FSN
        // order, and the partial D is lost.
        var link = new ArqLink(1);
        link.Receive(3, true, false, true, false, [0xC]).Should().BeEmpty();
        link.Receive(4, false, true, true, false, [0xC]).Should().BeEmpty();
        link.Receive(1, true, true, true, false, [0xB]).Should().BeEmpty();
        link.Receive(6, true, false, true, false, [0xD]).Should().BeEmpty();
        link.TakeHeldInOrder().Select(m => m.ToArray()).Should().BeEquivalentTo(new[] { new byte[] { 0xB }, new byte[] { 0xC, 0xC } }, o => o.WithStrictOrdering());
        link.TakeHeldInOrder().Should().BeEmpty();
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

