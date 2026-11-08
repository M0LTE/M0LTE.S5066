using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>The host.modem.radio object (core-architecture.md 12.11).</summary>
public class RadioSettingsTests
{
    private const string Minimal = """{ "capture_device": "plughw:CARD=Device,DEV=0", "playback_device": "plughw:CARD=Device,DEV=0", "ptt_device": "/dev/hidraw0" }""";

    [Fact]
    public void The_three_device_names_are_enough_and_the_rest_have_defaults()
    {
        RadioSettings s = RadioSettings.Parse(Obj(Minimal));
        s.Should().Be(new RadioSettings("plughw:CARD=Device,DEV=0", "plughw:CARD=Device,DEV=0", 48000, "/dev/hidraw0", 3, 250, 20, 0.5, false, null));
    }

    [Fact]
    public void Every_key_is_read()
    {
        RadioSettings s = RadioSettings.Parse(Obj("""
            { "capture_device": "a", "playback_device": "b", "sample_rate": 96000, "ptt_device": "/dev/hidraw3", "ptt_gpio": 4,
              "txdelay_ms": 120, "txtail_ms": 0, "tx_level": 0.05, "busy_detect": true, "rx_record": "/tmp/rx.wav" }
            """));
        s.Should().Be(new RadioSettings("a", "b", 96000, "/dev/hidraw3", 4, 120, 0, 0.05, true, "/tmp/rx.wav"));
    }

    [Theory]
    [InlineData("""{ "playback_device": "b", "ptt_device": "p" }""", "host.modem.radio.capture_device: is required")]
    [InlineData("""{ "capture_device": "a", "ptt_device": "p" }""", "host.modem.radio.playback_device: is required")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b" }""", "host.modem.radio.ptt_device: is required")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "volume": 3 }""", "host.modem.radio.volume: unknown key")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "sample_rate": 44100 }""", "host.modem.radio.sample_rate: must be a multiple*")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "sample_rate": 9600 }""", "host.modem.radio.sample_rate: must be from 19200 to 192000")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "tx_level": 0 }""", "host.modem.radio.tx_level: must be above 0 and at most 1")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "tx_level": 1.5 }""", "host.modem.radio.tx_level: must be above 0 and at most 1")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "ptt_gpio": 9 }""", "host.modem.radio.ptt_gpio: must be from 1 to 8")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "txdelay_ms": -1 }""", "host.modem.radio.txdelay_ms: must be from 0 to 2000")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "txdelay_ms": "300" }""", "host.modem.radio.txdelay_ms: must be a whole number")]
    [InlineData("""{ "capture_device": "a", "playback_device": "b", "ptt_device": "p", "busy_detect": "yes" }""", "host.modem.radio.busy_detect: must be true or false")]
    [InlineData("""{ "capture_device": "", "playback_device": "b", "ptt_device": "p" }""", "host.modem.radio.capture_device: must not be empty")]
    public void A_wrong_setting_is_refused_naming_the_key(string json, string message)
    {
        FluentActions.Invoking(() => RadioSettings.Parse(Obj(json))).Should().Throw<ConfigException>().WithMessage(message);
    }

    [Fact]
    public void The_host_passes_the_radio_object_through_as_written()
    {
        HostConfig c = HostConfig.Parse(Node("""{ "kind": "ms110d", "radio": """ + Minimal + " }"));
        c.Modem!.Radio.Should().NotBeNull();
        c.Modem.AudioListen.Should().BeNull();
        RadioSettings.Parse(c.Modem.Radio!).PttDevice.Should().Be("/dev/hidraw0");
    }

    [Fact]
    public void The_host_refuses_a_radio_and_udp_audio_together_and_unknown_modem_keys()
    {
        FluentActions.Invoking(() => HostConfig.Parse(Node("""{ "kind": "ms110d", "audio_listen": "127.0.0.1:7300", "radio": """ + Minimal + " }")))
            .Should().Throw<ConfigException>().WithMessage("host.modem.radio: cannot be set with audio_listen or audio_peer*");
        FluentActions.Invoking(() => HostConfig.Parse(Node("""{ "kind": "ms110d", "radio": 3 }""")))
            .Should().Throw<ConfigException>().WithMessage("host.modem.radio: must be an object");
        FluentActions.Invoking(() => HostConfig.Parse(Node("""{ "kind": "ms110d", "audio_lsiten": "127.0.0.1:7300" }""")))
            .Should().Throw<ConfigException>().WithMessage("host.modem.audio_lsiten: unknown key");
    }

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string Node(string modem) => """
        { "local_address": "0.0.1.1", "csma": { "mode": "jitter" }, "cas": { "max_links": 4, "link_request_retries": 3, "break_retries": 3 },
          "host": { "modem": 
        """ + modem + " } }";
}
