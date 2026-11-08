using System.Security.Cryptography;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.TestClient;

namespace M0LTE.S5066.T4.Tests;

/// <summary>The parts of s5066-test that need no node.</summary>
public class TestClientUnitTests
{
    [Fact]
    public void Every_u_pdu_starts_with_kind_transfer_id_and_sequence()
    {
        byte[] d = Wire.FileData(0x01020304, 7, [9, 9]);
        d.Should().Equal((byte)'D', 1, 2, 3, 4, 0, 0, 0, 7, 9, 9);
        Wire.TryReadPrefix(d, out WireKind kind, out uint id, out uint seq).Should().BeTrue();
        (kind, id, seq).Should().Be((WireKind.FileData, 0x01020304u, 7u));
        Wire.TryReadPrefix("hello\r\n"u8, out _, out _, out _).Should().BeFalse("not one of the tool's kinds");
    }

    [Fact]
    public void A_file_header_and_result_round_trip()
    {
        byte[] hash = SHA256.HashData([1, 2, 3]);
        byte[] h = Wire.FileHeader(5, 3, 1, hash, "a\u00e9b.txt");
        Wire.TryReadFileHeader(h, out long size, out int segs, out byte[] h2, out string name).Should().BeTrue();
        (size, segs, name).Should().Be((3L, 1, "a_b.txt"));
        h2.Should().Equal(hash);

        byte[] r = Wire.FileResult(5, false, 3, hash);
        Wire.TryReadFileResult(r, out bool match, out long got, out byte[] h3).Should().BeTrue();
        (match, got).Should().Be((false, 3L));
        h3.Should().Equal(hash);
    }

    [Fact]
    public void A_perf_message_has_the_size_asked_for_and_its_echo_is_short()
    {
        byte[] p = Wire.Perf(1, 2, 100, echo: true);
        p.Should().HaveCount(100);
        Wire.PerfWantsEcho(p).Should().BeTrue();
        Wire.PerfWantsEcho(Wire.Perf(1, 2, 10, echo: false)).Should().BeFalse();
        byte[] e = Wire.Echo(p);
        e.Should().HaveCount(Wire.PerfMinBytes);
        Wire.TryReadPrefix(e, out WireKind kind, out uint id, out uint seq).Should().BeTrue();
        (kind, id, seq).Should().Be((WireKind.Echo, 1u, 2u));
        FluentActions.Invoking(() => Wire.Perf(1, 2, 9, false)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("0.0.1.2", false, 0x102u)]
    [InlineData("16.0.0.7", true, 7u)]
    [InlineData("31.255.255.255", true, 0x0FFFFFFFu)]
    [InlineData("all", true, 0x0FFFFFFFu)]
    public void Addresses_parse_with_the_group_flag_as_a_29th_bit(string text, bool group, uint value)
    {
        SisAddress a = Addresses.Parse(text);
        (a.Group, a.Value).Should().Be((group, value));
        if (text != "all")
        {
            Addresses.Show(a).Should().Be(text);
        }
    }

    [Fact]
    public void Chat_lines_are_ascii_ending_cr_lf_within_the_mtu()
    {
        // O-0002, O-0003, O-0004.
        Station.EncodeChat("hi", 10).Should().Equal((byte)'h', (byte)'i', 13, 10);
        FluentActions.Invoking(() => Station.EncodeChat("caf\u00e9", 10)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => Station.EncodeChat("123456789", 10)).Should().Throw<ArgumentException>();
        Station.DecodeChat([(byte)'a', 7, 13, 10]).Should().Be("a?");
    }

    [Fact]
    public void Chat_to_one_node_is_arq_confirmed_and_to_a_group_non_arq()
    {
        // O-0016, O-0018.
        Station.ChatMode(Addresses.Parse("0.0.1.2")).Should().Be(new ServiceType(1, Confirm: 1, Order: 0));
        Station.ChatMode(Addresses.Broadcast).Should().Be(new ServiceType(2, Confirm: 0, Order: 1, MinRetx: 1));
    }

    [Fact]
    public void A_scenario_reads_snake_case_with_comments()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, """
            // two nodes
            { "nodes": { "a": "127.0.0.1:5066" },
              "steps": [ { "do": "perf", "node": "a", "to": "0.0.1.2", "count": 3, "timeout_s": 5, "segment_bytes": 100 }, ] }
            """);
        Scenario s = ScenarioRunner.Load(path);
        File.Delete(path);
        s.Steps.Should().ContainSingle();
        (s.Steps[0].Count, s.Steps[0].TimeoutS, s.Steps[0].SegmentBytes).Should().Be((3, 5.0, 100));
    }
}
