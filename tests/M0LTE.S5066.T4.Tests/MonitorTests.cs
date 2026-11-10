using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using M0LTE.Dsp;
using M0LTE.Ms110d;
using M0LTE.S5066.Client;
using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Core.Sis;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.TestClient.Monitor;

namespace M0LTE.S5066.T4.Tests;

/// <summary>
/// The receive-only monitor of s5066-test (P8-1; plan 11.6 part 2): a synthesised two-station
/// exchange in a WAV file, and a live two-node exchange over modem audio heard by a third station.
/// </summary>
public class MonitorTests
{
    private const uint A = 0x00000101;   // 0.0.1.1
    private const uint B = 0x00000102;   // 0.0.1.2
    private const uint Group = 0x00000001;

    [Fact]
    public void A_wav_of_two_stations_decodes_to_the_d_pdus_they_sent()
    {
        byte[] segment = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var data = new DataOnly(Header(A, B, eow: EowCodec.Encode(new SpeedUsedEow(new SpeedInterleaver(3, 6))), eot: 6), new DataFlags(true, true, true, false, false, false), 0, new Octets(segment), true);
        var mgmt = new Management(Header(A, B, eot: 5), false, true, false, 7, Octets.Empty);
        var ack = new AckOnly(Header(B, A, eot: 2), 1, AckData.BitmapEncoding, 0, Octets.Empty, null);
        var bcast = new NonArqData(Header(B, Group, eot: 2), new NonArqFields(42, false, true, 30, 0, 20), new Octets(new byte[30]), true);
        byte[] bad = DpduCodec.Encode(new DataOnly(Header(B, A, eot: 1), new DataFlags(true, true, true, false, false, false), 5, new Octets(new byte[20]), true));
        bad[^6] ^= 0x55;   // in the segment: the header CRC still passes, the data CRC fails

        byte[] burst1 = [.. DpduCodec.Encode(data), .. DpduCodec.Encode(mgmt)];
        byte[] burst2 = [.. DpduCodec.Encode(ack), .. DpduCodec.Encode(bcast), .. bad];
        float[] audio = [.. Silence(0.5), .. Modulate(burst1), .. Silence(1.0), .. Modulate(burst2), .. Silence(0.5)];
        string wav = Path.Combine(Path.GetTempPath(), $"s5066-monitor-{Guid.NewGuid():N}.wav");
        try
        {
            WriteWav48k(wav, audio);
            var text = new StringWriter();
            var json = new StringWriter();
            MonitorDecoder m = MonitorCommand.RunWav(wav, text, json);

            string[] lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string[] dpdus = lines.Where(l => l.Contains(" > ", StringComparison.Ordinal) && !l.StartsWith("LINK", StringComparison.Ordinal)).ToArray();
            dpdus.Should().HaveCount(5, string.Join("\n", lines));
            dpdus[0].Should().MatchRegex(@"^\+00:00:\d\d\.\d{3} WN6/S \d+\.\ddB 0\.0\.1\.1 > 0\.0\.1\.2 DATA-ONLY fsn=0 flags=SEI eow=used:WN6/S eot=6\(3\.0s\) len=100 crc=ok$");
            dpdus[1].Should().EndWith("0.0.1.1 > 0.0.1.2 MANAGEMENT id=7 flags=V eow=- eot=5(2.5s) len=0 crc=hdr-ok");
            dpdus[2].Should().EndWith("0.0.1.2 > 0.0.1.1 ACK-ONLY lwe=1 ack-offset=0 acks=bitmap:0B eow=- eot=2(1.0s) len=0 crc=hdr-ok");
            dpdus[3].Should().EndWith("0.0.1.2 > 0.0.0.1(grp) NON-ARQ cpdu-id=42 size=30 offset=0 rx-window=20 eow=- eot=2(1.0s) len=30 crc=ok");
            dpdus[4].Should().EndWith("0.0.1.2 > 0.0.1.1 DATA-ONLY fsn=5 flags=SEI eow=- eot=1(0.5s) len=20 crc=BAD");
            lines.Count(l => l.Contains("BURST", StringComparison.Ordinal) && l.Contains(" start WN6/S K7", StringComparison.Ordinal)).Should().Be(2);
            lines.Should().Contain(l => l.Contains("BURST 2 end eom", StringComparison.Ordinal) && l.EndsWith("3 D_PDUs, 2 with data (1 CRC bad), 0 cut, 0 bad headers", StringComparison.Ordinal));
            lines.Should().Contain("LINK 0.0.1.1 > 0.0.1.2: 1 bursts, 2 D_PDUs, 1 data (0 CRC bad, 100 bytes good), 0 acks, 0 cut; last WN6/S snr " + lines.First(l => l.StartsWith("LINK 0.0.1.1", StringComparison.Ordinal)).Split("snr ")[1]);
            m.Bursts.Should().Be(2);

            // JSON lines: the same records, one object a line.
            JsonElement[] records = json.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
            records.Select(r => r.GetProperty("kind").GetString()).Should().Equal(
                "burst_start", "dpdu", "dpdu", "burst_end", "burst_start", "dpdu", "dpdu", "dpdu", "burst_end", "link", "link", "link");
            JsonElement first = records[1];
            first.GetProperty("type").GetInt32().Should().Be(9);
            first.GetProperty("src").GetString().Should().Be("0.0.1.1");
            first.GetProperty("fsn").GetInt32().Should().Be(0);
            first.GetProperty("data_len").GetInt32().Should().Be(100);
            first.GetProperty("data_crc").GetString().Should().Be("ok");
            records[7].GetProperty("data_crc").GetString().Should().Be("bad");
            records[6].GetProperty("group").GetBoolean().Should().BeTrue();
        }
        finally
        {
            File.Delete(wav);
        }
    }

    [Fact]
    public async Task A_third_station_hears_every_d_pdu_two_nodes_send()
    {
        using var medium = new AudioMedium();
        var sent = new ConcurrentQueue<byte[]>();
        await using NodeHost a = Start(medium, "0.0.1.1", "0000000000000001", sent);
        await using NodeHost b = Start(medium, "0.0.1.2", "0000000000000002", sent);

        var heard = new ConcurrentQueue<MonitorDpdu>();
        var ended = new ConcurrentQueue<MonitorBurstEnd>();
        var monitor = new MonitorDecoder(new Collect(heard, ended));
        medium.CreateChannel().Start(chunk => monitor.Process(chunk.Span));

        await using SisClient ca = await SisClient.ConnectAsync(a.SisEndpoint!);
        await using SisClient cb = await SisClient.ConnectAsync(b.SisEndpoint!);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await ca.BindAsync(13, new ServiceType(1), cts.Token);
        await cb.BindAsync(13, new ServiceType(1), cts.Token);
        byte[] u = Encoding.ASCII.GetBytes("heard by the monitor, " + new string('x', 300));
        SendResult r = await ca.SendAsync(new UnidataSend(SisAddress.FromParts(0, 0, 1, 2), 13, u) { DeliveryMode = new ServiceType(1, Confirm: 1) }, cts.Token);
        r.Status.Should().Be(SendStatus.Confirmed);

        // Every burst sent has been heard to its end (the last one, an acknowledgement, may still be on the air).
        while (ended.Count < sent.Count && !cts.IsCancellationRequested)
        {
            await Task.Delay(200);
        }

        List<byte[]> expected = sent.SelectMany(DpdusIn).ToList();
        List<MonitorDpdu> got = heard.ToList();
        expected.Should().NotBeEmpty();
        got.Should().OnlyContain(d => d.Status == "ok");
        got.Select(d => Convert.ToHexString(d.Raw)).Should().Equal(expected.Select(Convert.ToHexString));
        got.Any(d => d.Pdu is DataOnly or DataAck or NonArqData).Should().BeTrue();
        got.Should().Contain(d => d.Header.Header.Src == B);
    }

    private static DpduHeader Header(uint src, uint dst, int eow = 0, int eot = 0) => new(eow, eot, DpduAddress.MinimumSize(dst, src), dst, src);

    private static float[] Silence(double seconds) => new float[(int)(seconds * 9600)];

    private static float[] Modulate(byte[] bytes)
    {
        var settings = new Ms110dTxSettings { WaveformNumber = 6, Interleaver = Ms110dInterleaverKind.Short, ConstraintLength = 7, AppendEom = true, AppendEot = true };
        byte[] bits = new byte[bytes.Length * 8];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)((bytes[i / 8] >> (i % 8)) & 1);
        }

        return new Ms110dModulator(settings).Modulate(bits);
    }

    /// <summary>Writes 9600 Hz audio as a 48 kHz 16-bit PCM WAV with a little noise, so the reader's decimation is exercised.</summary>
    private static void WriteWav48k(string path, float[] audio)
    {
        var up = new Upsampler(48000, 5);
        var samples = new float[up.OutputLength(audio.Length)];
        up.Process(audio, samples);
        var noise = new Random(7);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8);
        w.Write(36 + (samples.Length * 2));
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(48000);
        w.Write(96000);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (float s in samples)
        {
            float v = s + (float)((noise.NextDouble() - 0.5) * 0.01);
            w.Write((short)Math.Clamp(v * 32767, -32768, 32767));
        }
    }

    /// <summary>The D_PDUs in one transmission, as the bytes went to the modem.</summary>
    private static IEnumerable<byte[]> DpdusIn(byte[] tx)
    {
        int pos = 0;
        while (pos + 1 < tx.Length)
        {
            if (tx[pos] == 0x90 && tx[pos + 1] == 0xEB)
            {
                DecodeResult<Dpdu> d = DpduCodec.DecodeFirst(tx.AsSpan(pos), PeerEdition.Any);
                if (d.IsSuccess)
                {
                    yield return tx.AsSpan(pos, d.Consumed).ToArray();
                    pos += d.Consumed;
                    continue;
                }
            }

            pos++;
        }
    }

    private static NodeHost Start(AudioMedium medium, string address, string seed, ConcurrentQueue<byte[]> sent)
    {
        HostConfig c = HostConfig.Parse($$"""
            {
              "local_address": "{{address}}",
              "prng_seed": "{{seed}}",
              "csma": { "mode": "jitter", "num_cont_slots": 4, "cont_slot_width_ms": 1000, "lbt_wait_dcd_ms": 2000, "lbt_wait_vdcd_ms": 500 },
              "drc": { "default_speed": { "wn": 6, "interleaver": "short" } },
              "cas": { "max_links": 4, "link_request_retries": 5, "break_retries": 3 },
              "dts": { "max_transmission_ms": 6000 },
              "host": { "sis_listen": "127.0.0.1:0" }
            }
            """);
        var modem = new Recording(new Ms110dHostModem(medium.CreateChannel()), sent);
        var host = new NodeHost(c.Node, c.PrngSeed, modem, new NodeHostOptions { SisEndpoint = c.SisEndpoint });
        host.Start();
        return host;
    }

    /// <summary>A modem port that keeps a copy of every transmission: what the node sent.</summary>
    private sealed class Recording(IHostModem inner, ConcurrentQueue<byte[]> sent) : IHostModem, IHostModemWrapper
    {
        public IHostModem Inner => inner;

        public BurstPlan PlanBurst(int wn, Interleaver interleaver, int byteCount) => inner.PlanBurst(wn, interleaver, byteCount);

        public void Start(IModemEvents events) => inner.Start(events);

        public void Transmit(ReadOnlyMemory<byte> bytes, int wn, Interleaver interleaver, int k, bool eom)
        {
            sent.Enqueue(bytes.ToArray());
            inner.Transmit(bytes, wn, interleaver, k, eom);
        }

        public void TerminateReceive() => inner.TerminateReceive();

        public void SetMaxInputBlocks(int n) => inner.SetMaxInputBlocks(n);
    }

    private sealed class Collect(ConcurrentQueue<MonitorDpdu> heard, ConcurrentQueue<MonitorBurstEnd> ended) : IMonitorSink
    {
        public void BurstStarted(MonitorBurst burst)
        {
        }

        public void Dpdu(MonitorDpdu dpdu) => heard.Enqueue(dpdu);

        public void BurstEnded(MonitorBurstEnd end) => ended.Enqueue(end);
    }
}
