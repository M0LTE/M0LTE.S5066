using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Host.Station;

namespace M0LTE.S5066.Host.Tests;

/// <summary>The station status page (P8-2; core-architecture.md 12.12), on two loopback hosts.</summary>
public class StationStatusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task The_status_shows_the_link_the_peer_its_waveforms_and_the_bursts_and_is_served_over_http()
    {
        var medium = new LoopbackMedium();
        var status = new StationStatus();
        await using NodeHost a = TestNodes.StartLoopbackHost(medium, "0.0.1.1", "0000000000000001", new NodeHostOptions { Status = status });
        await using NodeHost b = TestNodes.StartLoopbackHost(medium, "0.0.1.2", "0000000000000002");
        await using StatusServer server = new(new IPEndPoint(IPAddress.Loopback, 0), status);
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(Timeout);
        await ca.BindAsync(5, new ServiceType(1), cts.Token);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);

        SendResult r = await ca.SendAsync(
            new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, Encoding.ASCII.GetBytes("status test\r\n")) { DeliveryMode = new ServiceType(1, Confirm: 1) }, cts.Token);
        r.Status.Should().Be(SendStatus.Confirmed);

        JsonObject s = status.Snapshot();
        s["node"]!.GetValue<string>().Should().Be("0.0.1.1");
        s["ptt"].Should().BeNull("no radio");
        s["cw_id"].Should().BeNull();
        JsonObject peer = s["peers"]!.AsArray().Single()!.AsObject();
        peer["peer"]!.GetValue<string>().Should().Be("0.0.1.2");
        peer["link"]!.GetValue<string>().Should().Be("linked");
        peer["tx_waveform"]!.GetValue<string>().Should().StartWith("wn");
        peer["rx_waveform"]!.GetValue<string>().Should().StartWith("wn");
        peer["last_heard"].Should().NotBeNull();
        peer["queued_arq"]!.GetValue<int>().Should().Be(0, "the U_PDU is confirmed, so nothing is held");
        JsonArray bursts = s["bursts"]!.AsArray();
        bursts.Select(x => x!["dir"]!.GetValue<string>()).Should().Contain(["tx", "rx"]);
        bursts.Where(x => x!["dir"]!.GetValue<string>() == "tx").Should().OnlyContain(x => x!["peers"]!.AsArray().Any(p => p!.GetValue<string>() == "0.0.1.2"));

        using var http = new HttpClient { BaseAddress = new Uri($"http://{server.LocalEndpoint}/"), Timeout = Timeout };
        JsonNode served = JsonNode.Parse(await http.GetStringAsync(new Uri("status.json", UriKind.Relative), cts.Token))!;
        served["node"]!.GetValue<string>().Should().Be("0.0.1.1");
        string page = await http.GetStringAsync(new Uri("/", UriKind.Relative), cts.Token);
        page.Should().Contain("status.json");
        page.All(c => c < 128).Should().BeTrue("the page is ASCII");
        (await http.GetAsync(new Uri("nothing", UriKind.Relative), cts.Token)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void The_cw_id_and_ptt_appear_once_a_radio_sets_them()
    {
        var status = new StationStatus();
        var schedule = new CwIdSchedule(atStart: true, everyMinutes: 10);
        long t0 = DateTimeOffset.Parse("2026-11-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        schedule.Keyup(t0).Should().BeTrue();
        status.SetCwId(new CwIdSettings("DE M0XYZ", 20, 1800, null, true, 10), schedule);
        status.SetPtt(true);

        JsonObject s = status.Snapshot();
        s["ptt"]!.GetValue<string>().Should().Be("keyed");
        JsonObject cw = s["cw_id"]!.AsObject();
        cw["text"]!.GetValue<string>().Should().Be("DE M0XYZ");
        cw["sent"]!.GetValue<long>().Should().Be(1);
        cw["last_id"]!.GetValue<string>().Should().Be("2026-11-10T12:00:00.000Z");
        cw["periodic_due_from"]!.GetValue<string>().Should().Be("2026-11-10T12:10:00.000Z");
        cw["start_id_due_from"]!.GetValue<string>().Should().Be("2026-11-10T12:10:00.000Z");
    }
}
