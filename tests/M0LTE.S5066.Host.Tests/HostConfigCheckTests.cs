using System.Text.Json.Nodes;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>
/// Issue #83: a hosted node refuses at load a configuration it would accept and then stop on, with
/// one line naming the setting (interp B-0454).
/// </summary>
public class HostConfigCheckTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData("csma", "mode")]
    [InlineData("cas", "max_links")]
    [InlineData("cas", "link_request_retries")]
    [InlineData("cas", "break_retries")]
    public void A_file_without_a_setting_the_standard_gives_no_value_is_refused_at_load(string section, string key)
    {
        // D7, B-0193, B-0222, B-0288; interp B-0454.
        JsonObject file = LoopbackFile();
        ((JsonObject)file[section]!).Remove(key);

        Action load = () => HostConfig.Parse(file.ToJsonString());

        load.Should().Throw<MissingSettingException>()
            .Where(e => e.Setting == $"{section}.{key}" && e.Message.StartsWith($"{section}.{key}: not set; add ", StringComparison.Ordinal) && !e.Message.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void Slotted_mode_without_a_slot_position_is_refused_at_load_and_with_one_is_accepted()
    {
        // K-0104: no default; slotted mode needs it.
        JsonObject file = LoopbackFile();
        file["csma"]!["mode"] = "slotted";

        Action load = () => HostConfig.Parse(file.ToJsonString());
        load.Should().Throw<MissingSettingException>().Where(e => e.Setting == "csma.node_slot_position");

        file["csma"]!["node_slot_position"] = 2;
        HostConfig.Parse(file.ToJsonString()).Node.Csma.NodeSlotPosition.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void Slotted_mode_with_a_slot_position_out_of_range_is_refused_at_load(int position)
    {
        // K-0104: 1 to NUM_CONT_SLOTS; the core checks it only at the first slotted contention.
        JsonObject file = LoopbackFile();
        file["csma"]!["mode"] = "slotted";
        file["csma"]!["node_slot_position"] = position;

        Action load = () => HostConfig.Parse(file.ToJsonString());

        load.Should().Throw<ConfigException>()
            .Where(e => e.Setting == "csma.node_slot_position" && e.Message.Contains($"got {position}", StringComparison.Ordinal) && !e.Message.Contains('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void The_timers_the_core_derives_need_nothing_in_the_file()
    {
        // B-0420, A-0321, C-1120: null timers are derived, so a file without them is complete.
        JsonObject file = LoopbackFile();
        JsonObject cas = (JsonObject)file["cas"]!;
        cas.Remove("link_request_timeout_ms");
        file.Remove("sis");
        ((JsonObject)file["dts"]!).Remove("ack_wait_ms");

        HostConfig c = HostConfig.Parse(file.ToJsonString());

        c.Node.Cas.LinkRequestTimeoutMs.Should().BeNull();
        c.Node.LinkRequestTimeoutMs().Should().Be(c.Node.DerivedAnswerWaitMs());
    }

    [Fact]
    public void A_node_host_refuses_a_configuration_built_without_the_file_reader()
    {
        // The path pdn-soundmodem takes: ConfigReader, then NodeHost, with no HostConfig.
        JsonObject merged = HostConfig.Merge(HostConfig.Defaults(), Without(LoopbackFile(), "host"));
        ((JsonObject)merged["cas"]!)["break_retries"] = null;
        NodeConfig node = ConfigReader.Read(merged);
        var medium = new LoopbackMedium();

        Action make = () => _ = new NodeHost(node, 1, medium.CreateModem(node.Modem));

        make.Should().Throw<MissingSettingException>().Where(e => e.Setting == "cas.break_retries");
    }

    [Fact]
    public async Task A_run_time_change_that_drops_a_setting_is_refused_and_the_node_keeps_running()
    {
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1");
        NodeConfig bad = a.Config with { Cas = a.Config.Cas with { LinkRequestRetries = null } };

        Action change = () => a.UpdateConfig(bad);

        change.Should().Throw<MissingSettingException>().Where(e => e.Setting == "cas.link_request_retries");
        a.Config.Cas.LinkRequestRetries.Should().Be(5);
        a.Completion.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void A_default_speed_the_loopback_modem_cannot_plan_is_refused_when_the_host_is_made()
    {
        // core-architecture.md 5.3: every transmission uses drc.default_speed until phase 7.
        JsonObject file = LoopbackFile();
        file["drc"]!["default_speed"]!["wn"] = 7;
        HostConfig c = HostConfig.Parse(file.ToJsonString());
        var medium = new LoopbackMedium();

        Action make = () => _ = new NodeHost(c.Node, c.PrngSeed, medium.CreateModem(c.Node.Modem));

        make.Should().Throw<ConfigException>().Where(e => e.Setting == "drc.default_speed" && e.Message.Contains("wn 7 short", StringComparison.Ordinal));
    }

    [Fact]
    public void A_default_speed_the_110d_modem_does_not_have_is_refused_when_the_host_is_made()
    {
        JsonObject file = LoopbackFile();
        file["drc"]!["default_speed"]!["wn"] = 99;
        HostConfig c = HostConfig.Parse(file.ToJsonString());
        var medium = new AudioMedium();

        Action make = () => _ = new NodeHost(c.Node, c.PrngSeed, new Ms110dHostModem(medium.CreateChannel()));

        make.Should().Throw<ConfigException>().Where(e => e.Setting == "drc.default_speed");
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(720, false)]
    [InlineData(721, true)]
    [InlineData(1000, true)]
    public void A_contention_slot_no_longer_than_the_110d_preamble_is_refused_when_the_host_is_made(int width, bool accepted)
    {
        // K-0048, issue #86: the slot must outlast the channel-sensing delay, which is at least the
        // 720 ms preamble the 110D modem needs before it declares carrier. The old bench files had 200.
        JsonObject file = LoopbackFile();
        file["csma"]!["cont_slot_width_ms"] = width;
        HostConfig c = HostConfig.Parse(file.ToJsonString());
        var medium = new AudioMedium();

        Action make = () => _ = new NodeHost(c.Node, c.PrngSeed, new Ms110dHostModem(medium.CreateChannel()));

        if (accepted)
        {
            make.Should().NotThrow();
        }
        else
        {
            make.Should().Throw<ConfigException>()
                .Where(e => e.Setting == "csma.cont_slot_width_ms" && e.Message.Contains("720 ms preamble", StringComparison.Ordinal) && !e.Message.Contains('\n', StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_run_time_change_to_a_slot_no_longer_than_the_preamble_is_refused()
    {
        // K-0048 on the loopback modem: its preamble is 40 ms.
        var medium = new LoopbackMedium();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1");
        NodeConfig bad = a.Config with { Csma = a.Config.Csma with { ContSlotWidthMs = 40 } };

        Action change = () => a.UpdateConfig(bad);

        change.Should().Throw<ConfigException>().Where(e => e.Setting == "csma.cont_slot_width_ms");
        a.Config.Csma.ContSlotWidthMs.Should().Be(50);
    }

    [Fact]
    public async Task An_unanswered_link_request_fails_the_link_and_the_node_keeps_running()
    {
        // The issue's case: before the fix a node with cas.link_request_retries unset started and then
        // stopped here. Now the file must set it, and the call fails cleanly after the repeats (B-0223,
        // B-0407) with the host still running.
        JsonObject file = LoopbackFile();
        file["cas"]!["link_request_timeout_ms"] = 300;
        file["cas"]!["link_request_retries"] = 1;
        HostConfig c = HostConfig.Parse(file.ToJsonString());
        var medium = new LoopbackMedium();
        await using var a = new NodeHost(c.Node, c.PrngSeed, medium.CreateModem(c.Node.Modem), new NodeHostOptions { SisEndpoint = c.SisEndpoint });
        var unlinked = new TaskCompletionSource();
        bool called = false;
        a.LinkStateChanged += (_, s) =>
        {
            if (s == LinkStateName.Calling)
            {
                called = true;
            }
            else if (s == LinkStateName.Unlinked && called)
            {
                unlinked.TrySetResult();
            }
        };
        a.Start();
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        using var cts = new CancellationTokenSource(Timeout);
        await ca.BindAsync(5, new ServiceType(1), cts.Token);

        _ = ca.SendAsync(new UnidataSend(SisAddress.FromParts(0, 0, 1, 9), 5, new byte[] { 1 }), cts.Token);
        await unlinked.Task.WaitAsync(cts.Token);
        await Task.Delay(200, cts.Token);

        a.Completion.IsCompleted.Should().BeFalse("the node is still running");
    }

    private static JsonObject LoopbackFile() => (JsonObject)JsonNode.Parse(TestNodes.LoopbackJson("0.0.1.1", "0000000000000001"))!;

    private static JsonObject Without(JsonObject o, string key)
    {
        o.Remove(key);
        return o;
    }
}
