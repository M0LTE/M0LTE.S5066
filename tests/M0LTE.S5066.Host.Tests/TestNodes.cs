using System.Net;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>Configurations and hosts for the socket tests.</summary>
internal static class TestNodes
{
    /// <summary>
    /// A node on a fast byte-level loopback: short bursts (WN 6 short: 64-byte blocks of 20 ms, 40 ms
    /// preamble, 10 ms flush) and short CSMA waits, so an ARQ exchange takes well under a second.
    /// </summary>
    public static string LoopbackJson(string address, string seed) => $$"""
        {
          "local_address": "{{address}}",
          "prng_seed": "{{seed}}",
          "csma": { "mode": "jitter", "num_cont_slots": 4, "cont_slot_width_ms": 30, "lbt_wait_dcd_ms": 300, "lbt_wait_vdcd_ms": 100 },
          "modem": {
            "preamble_ms": 40, "flush_ms": 10,
            "waveforms": [ { "wn": 6, "interleaver": "short", "block_bytes": 64, "block_ms": 20 } ]
          },
          "drc": { "default_speed": { "wn": 6, "interleaver": "short" } },
          "cas": { "max_links": 4, "link_request_timeout_ms": 1500, "link_request_retries": 5, "break_retries": 3 },
          "sis": { "soft_link_idle_ms": 60000 },
          "dts": { "max_transmission_ms": 2000, "ack_wait_ms": 1500 },
          "host": { "sis_listen": "127.0.0.1:0" }
        }
        """;

    public static HostConfig Loopback(string address, string seed = "0000000000000001") => HostConfig.Parse(LoopbackJson(address, seed));

    public static NodeHost StartLoopbackHost(LoopbackMedium medium, string address, string seed = "0000000000000001", NodeHostOptions? options = null)
    {
        HostConfig c = Loopback(address, seed);
        var host = new NodeHost(c.Node, c.PrngSeed, medium.CreateModem(c.Node.Modem),
            (options ?? new NodeHostOptions()) with { SisEndpoint = c.SisEndpoint });
        host.Start();
        return host;
    }

    public static IPEndPoint Endpoint(NodeHost host) => host.SisEndpoint ?? throw new InvalidOperationException("no SIS server");
}
