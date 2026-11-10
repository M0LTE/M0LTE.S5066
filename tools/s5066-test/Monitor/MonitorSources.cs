using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using M0LTE.Dsp;
using M0LTE.Radio.Audio;
using M0LTE.S5066.Radio;
using Packet.SoundModem.Channel;

namespace M0LTE.S5066.TestClient.Monitor;

/// <summary>The monitor's audio sources. None of them can transmit.</summary>
internal static class MonitorSources
{
    /// <summary>The modem's sample rate.</summary>
    public const int Rate = 9600;

    /// <summary>
    /// Reads a WAV file and feeds it to <paramref name="feed"/> at 9600 Hz, in 20 ms chunks, as
    /// fast as the demodulator takes it. PCM 16, 24 or 32 bit or 32-bit float; the first channel of
    /// several; 9600 Hz or a whole multiple of it, such as 48000 Hz.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a WAV file this can read; the message says why.</exception>
    public static void ReadWav(string path, Action<ReadOnlySpan<float>> feed)
    {
        float[] samples = LoadWav(path, out int rate);
        if (rate % Rate != 0)
        {
            throw new InvalidDataException($"{path} is at {rate} Hz; the monitor needs 9600 Hz or a whole multiple of it (48000 Hz is fine). Resample it, for example: sox in.wav -r 48000 out.wav");
        }

        int factor = rate / Rate;
        int chunk = rate / 50;
        if (factor == 1)
        {
            for (int i = 0; i < samples.Length; i += chunk)
            {
                feed(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)));
            }

            return;
        }

        var decimator = new Decimator(rate, factor);
        var output = new float[decimator.MaxOutput(chunk)];
        for (int i = 0; i < samples.Length; i += chunk)
        {
            int n = decimator.Process(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)), output);
            if (n > 0)
            {
                feed(output.AsSpan(0, n));
            }
        }
    }

    /// <summary>Reads the first channel of a WAV file as floats in [-1, 1].</summary>
    internal static float[] LoadWav(string path, out int rate)
    {
        byte[] file = File.ReadAllBytes(path);
        ReadOnlySpan<byte> b = file;
        if (b.Length < 12 || !b[..4].SequenceEqual("RIFF"u8) || !b[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"{path} is not a WAV file");
        }

        int format = 0, channels = 0, bits = 0;
        rate = 0;
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            ReadOnlySpan<byte> id = b.Slice(pos, 4);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b[(pos + 4)..]), (uint)(b.Length - pos - 8));
            ReadOnlySpan<byte> body = b.Slice(pos + 8, size);
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == 0xFFFE && size >= 26)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]);   // WAVE_FORMAT_EXTENSIBLE: the sub-format
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels == 0)
                {
                    throw new InvalidDataException($"{path}: no fmt chunk before the data");
                }

                return Samples(path, body, format, channels, bits);
            }

            pos += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"{path}: no data chunk");
    }

    private static float[] Samples(string path, ReadOnlySpan<byte> data, int format, int channels, int bits)
    {
        int width = bits / 8;
        int frame = width * channels;
        if (frame == 0 || !((format == 1 && bits is 16 or 24 or 32) || (format == 3 && bits == 32)))
        {
            throw new InvalidDataException($"{path}: format {format}, {bits} bit; the monitor reads PCM 16, 24 or 32 bit or 32-bit float");
        }

        var samples = new float[data.Length / frame];
        for (int i = 0; i < samples.Length; i++)
        {
            ReadOnlySpan<byte> s = data.Slice(i * frame, width);
            samples[i] = (format, bits) switch
            {
                (3, _) => BinaryPrimitives.ReadSingleLittleEndian(s),
                (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                (_, 24) => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            };
        }

        return samples;
    }

    /// <summary>
    /// Listens for the UDP audio s5066d sends (9600 Hz mono, little-endian 32-bit float, one
    /// datagram per 20 ms; <c>UdpAudioChannel</c>) until stopped. Sends nothing.
    /// </summary>
    public static async Task ListenUdpAsync(IPEndPoint listen, Action<ReadOnlySpan<float>> feed, CancellationToken stop)
    {
        using var udp = new UdpClient(listen);
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            feed(MemoryMarshal.Cast<byte, float>(r.Buffer.AsSpan(0, r.Buffer.Length & ~3)));
        }
    }

    /// <summary>
    /// Captures from a sound card through the radio project's audio path (the one s5066d uses,
    /// <see cref="RadioAudioChannel"/>), with no playback device and no PTT: the monitor cannot key
    /// a radio. Runs until stopped or the card fails.
    /// </summary>
    /// <exception cref="RadioException">The card could not be opened.</exception>
    public static async Task CaptureAsync(string device, int sampleRate, Action<ReadOnlySpan<float>> feed, Action<string> log, CancellationToken stop)
    {
        AlsaAudioInput input;
        try
        {
            input = new AlsaAudioInput(device, sampleRate);
        }
        catch (DllNotFoundException)
        {
            throw new RadioException("cannot load libasound.so.2, the ALSA library: install it (Debian: apt install libasound2t64)");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            throw new RadioException($"cannot open {device}: {e.Message}");
        }

        if (input.ConfigurationWarning is { } warning)
        {
            log("card: " + warning);
        }

        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var audio = new RadioAudioChannel(input, new NoOutput(sampleRate), new NoPtt(), 0, 0, log);
        audio.Failed += e => failed.TrySetResult(e);
        audio.Start(chunk => feed(chunk.Span));
        Task stopped = Task.Delay(Timeout.Infinite, stop);
        if (await Task.WhenAny(failed.Task, stopped).ConfigureAwait(false) == failed.Task)
        {
            throw new RadioException($"the sound card stopped: {failed.Task.Result.Message}");
        }
    }

    /// <summary>A playback device that refuses every write: the monitor never transmits.</summary>
    private sealed class NoOutput(int sampleRate) : IAudioOutput
    {
        public int SampleRate { get; } = sampleRate;

        public void Write(ReadOnlySpan<float> samples) => throw new InvalidOperationException("the monitor is receive only");

        public void Drain()
        {
        }
    }

    /// <summary>A PTT line that refuses to key: the monitor never transmits.</summary>
    private sealed class NoPtt : IPttControl
    {
        public void Key() => throw new InvalidOperationException("the monitor is receive only");

        public void Unkey()
        {
        }
    }
}
