using System.Text;
using M0LTE.Ms110d;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

namespace M0LTE.S5066.Host.Tests;

/// <summary>The M0LTE.Ms110d adapter (core-architecture.md 12.6), and two hosts over real modem audio.</summary>
public class Ms110dHostTests
{
    [Theory]
    [InlineData(6, Interleaver.Short, 0)]
    [InlineData(6, Interleaver.Short, 500)]
    [InlineData(2, Interleaver.Long, 200)]
    [InlineData(13, Interleaver.Medium, 40)]
    [InlineData(8, Interleaver.UltraShort, 3000)]
    public void The_plan_is_never_early_and_matches_the_modulated_audio(int wn, Interleaver il, int bytes)
    {
        var modem = new Ms110dHostModem(new AudioMedium().CreateChannel());
        BurstPlan plan = modem.PlanBurst(wn, il, bytes);

        var settings = new Ms110dTxSettings { WaveformNumber = wn, Interleaver = Kind(il), AppendEom = true, AppendEot = true };
        float[] audio = new Ms110dModulator(settings).Modulate(Ms110dHostModem.ToBits(new byte[bytes]));
        double audioMs = audio.Length / 9.6;

        plan.DurationMs.Should().Be(plan.PreambleMs + (plan.Blocks * (long)plan.BlockMs) + plan.FlushMs);
        plan.DurationMs.Should().BeGreaterThanOrEqualTo((long)Math.Ceiling(audioMs), "the plan is never early (C-0891)");
        plan.DurationMs.Should().BeLessThanOrEqualTo((long)Math.Ceiling(audioMs) + 3, "each of the three parts rounds up by under 1 ms");
        (plan.BytesPerBlock * plan.Blocks * 8).Should().BeGreaterThanOrEqualTo((bytes * 8) + 32, "the blocks hold the bytes and the 32-bit EOM");
    }

    [Fact]
    public void Very_short_and_very_long_are_refused()
    {
        var modem = new Ms110dHostModem(new AudioMedium().CreateChannel());
        FluentActions.Invoking(() => modem.PlanBurst(6, Interleaver.VeryShort, 10)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => modem.PlanBurst(6, Interleaver.VeryLong, 10)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Bits_go_least_significant_first_and_pack_back()
    {
        byte[] bytes = [0x01, 0x80, 0xA5];
        byte[] bits = Ms110dHostModem.ToBits(bytes);
        bits[..8].Should().Equal(1, 0, 0, 0, 0, 0, 0, 0);
        bits[8..16].Should().Equal(0, 0, 0, 0, 0, 0, 0, 1);
        Ms110dHostModem.ToBytes(bits).Should().Equal(bytes);
    }

    [Fact]
    public async Task Transmit_returns_before_modulation_and_gates_receive_at_once()
    {
        // Issue #80: modulation runs off the caller's thread (the host's loop), so the core is never
        // held up by a long burst; receive is gated from the moment Transmit returns (12.5 rule 3).
        using var release = new ManualResetEventSlim();
        var modulating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = new FakeAudio();
        var events = new RecordingModemEvents();
        var modem = new Ms110dHostModem(audio, 0.5f, null, (settings, bits) =>
        {
            settings.WaveformNumber.Should().Be(6);
            bits.Length.Should().Be(24);
            modulating.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            return new float[960];
        });
        modem.Start(events);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        modem.Transmit(new byte[] { 1, 2, 3 }, 6, Interleaver.Short, 7, true);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        await modulating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        audio.Played.Reader.Count.Should().Be(0, "the burst is still being modulated");
        FluentActions.Invoking(() => modem.Transmit(new byte[] { 1 }, 6, Interleaver.Short, 7, true))
            .Should().Throw<InvalidOperationException>("a burst is already being sent (12.5 rule 4)");

        release.Set();
        (float[] samples, Action played) = await audio.Played.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        samples.Length.Should().Be(960);
        events.TransmitDones.Should().Be(0);
        played();
        events.TransmitDones.Should().Be(1);
    }

    [Fact]
    public void A_bad_interleaver_still_throws_to_the_caller()
    {
        var modem = new Ms110dHostModem(new FakeAudio());
        modem.Start(new RecordingModemEvents());
        FluentActions.Invoking(() => modem.Transmit(new byte[] { 1 }, 6, Interleaver.VeryShort, 7, true)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Two_hosts_exchange_a_confirmed_arq_u_pdu_over_modem_audio()
    {
        using var medium = new AudioMedium();
        await using NodeHost a = Start(medium, "0.0.1.1", "0000000000000001");
        await using NodeHost b = Start(medium, "0.0.1.2", "0000000000000002");
        await using SisClient ca = await SisClient.ConnectAsync(TestNodes.Endpoint(a));
        await using SisClient cb = await SisClient.ConnectAsync(TestNodes.Endpoint(b));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await ca.BindAsync(5, new ServiceType(1), cts.Token);
        await cb.BindAsync(5, new ServiceType(1), cts.Token);

        byte[] u = Encoding.ASCII.GetBytes("over MIL-STD-188-110D audio\r\n");
        SendResult r = await ca.SendAsync(
            new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 5, u) { DeliveryMode = new ServiceType(1, Confirm: 1) }, cts.Token);
        SisUnidataIndication got = await cb.Received.ReadAsync(cts.Token);

        got.UPdu.ToArray().Should().Equal(u);
        r.Status.Should().Be(SendStatus.Confirmed);
    }

    private static NodeHost Start(AudioMedium medium, string address, string seed)
    {
        HostConfig c = HostConfig.Parse($$"""
            {
              "local_address": "{{address}}",
              "prng_seed": "{{seed}}",
              "csma": { "mode": "jitter", "num_cont_slots": 4, "cont_slot_width_ms": 200, "lbt_wait_dcd_ms": 2000, "lbt_wait_vdcd_ms": 500 },
              "drc": { "default_speed": { "wn": 6, "interleaver": "short" } },
              "cas": { "max_links": 4, "link_request_timeout_ms": 8000, "link_request_retries": 5, "break_retries": 3 },
              "dts": { "max_transmission_ms": 6000, "ack_wait_ms": 8000 },
              "host": { "sis_listen": "127.0.0.1:0" }
            }
            """);
        var host = new NodeHost(c.Node, c.PrngSeed, new Ms110dHostModem(medium.CreateChannel()), new NodeHostOptions { SisEndpoint = c.SisEndpoint });
        host.Start();
        return host;
    }

    private static Ms110dInterleaverKind Kind(Interleaver il) => il switch
    {
        Interleaver.UltraShort => Ms110dInterleaverKind.UltraShort,
        Interleaver.Short => Ms110dInterleaverKind.Short,
        Interleaver.Medium => Ms110dInterleaverKind.Medium,
        _ => Ms110dInterleaverKind.Long,
    };

    private sealed class FakeAudio : IAudioChannel
    {
        public System.Threading.Channels.Channel<(float[] Samples, Action Played)> Played { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<(float[], Action)>();

        public void Start(Action<ReadOnlyMemory<float>> received)
        {
        }

        public void Play(float[] samples, Action played) => Played.Writer.TryWrite((samples, played));
    }

    private sealed class RecordingModemEvents : IModemEvents
    {
        private int _transmitDones;

        public int TransmitDones => Volatile.Read(ref _transmitDones);

        public void BurstStarted()
        {
        }

        public void BytesReceived(ReadOnlyMemory<byte> bytes)
        {
        }

        public void BurstEnded(BurstEndReason reason, int wn, Interleaver interleaver, int k, int? snrTenthDb, int blocks)
        {
        }

        public void TransmitDone() => Interlocked.Increment(ref _transmitDones);

        public void ChannelBusy(bool busy)
        {
        }
    }
}
