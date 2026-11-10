using System.Net;
using M0LTE.S5066.Radio;

namespace M0LTE.S5066.TestClient.Monitor;

/// <summary><c>s5066-test monitor</c>: the receive-only monitor (plan 11.6 part 2; docs/s5066-test.md).</summary>
internal static class MonitorCommand
{
    /// <summary>Runs the monitor on one source until the file ends or it is stopped, then prints the link summary.</summary>
    /// <returns>0, or 2 for a usage error.</returns>
    public static async Task<int> RunAsync(string? wav, string? udp, string? card, int cardRate, string? jsonPath, bool quiet, CancellationToken stop)
    {
        int sources = (wav is null ? 0 : 1) + (udp is null ? 0 : 1) + (card is null ? 0 : 1);
        if (sources != 1)
        {
            Console.Error.WriteLine("s5066-test monitor: give one of --wav file, --udp host:port or --card device");
            return 2;
        }

        using StreamWriter? json = jsonPath is null ? null : new StreamWriter(jsonPath) { AutoFlush = wav is null };
        TextWriter? text = quiet ? null : Console.Out;
        MonitorDecoder? decoder = null;
        var clock = new MonitorClock(wav is null, () => decoder!.SamplesProcessed);
        var output = new MonitorOutput(text, json, clock);
        decoder = new MonitorDecoder(output);
        object gate = new();
        void Feed(ReadOnlySpan<float> samples)
        {
            lock (gate)
            {
                decoder.Process(samples);
            }
        }

        if (wav is not null)
        {
            MonitorSources.ReadWav(wav, Feed);
        }
        else if (udp is not null)
        {
            (string host, int port) = Addresses.ParseEndpoint(udp);
            var endpoint = new IPEndPoint(IPAddress.Parse(host), port);
            Console.Error.WriteLine($"s5066-test monitor: listening for UDP audio on {endpoint}; Ctrl-C to stop");
            await MonitorSources.ListenUdpAsync(endpoint, Feed, stop);
        }
        else
        {
            Console.Error.WriteLine($"s5066-test monitor: listening on {card} at {cardRate} Hz, receive only; Ctrl-C to stop");
            await MonitorSources.CaptureAsync(card!, cardRate, Feed, line => Console.Error.WriteLine(line), stop);
        }

        lock (gate)
        {
            decoder.Finish();
            output.Summary(decoder);
        }

        return 0;
    }

    /// <summary>Runs the monitor over a WAV file into writers; for tests.</summary>
    internal static MonitorDecoder RunWav(string path, TextWriter? text, TextWriter? json)
    {
        MonitorDecoder? decoder = null;
        var output = new MonitorOutput(text, json, new MonitorClock(false, () => decoder!.SamplesProcessed));
        decoder = new MonitorDecoder(output);
        MonitorSources.ReadWav(path, s => decoder.Process(s));
        decoder.Finish();
        output.Summary(decoder);
        return decoder;
    }

    /// <summary>Whether an exception is one the monitor reports as a plain error line.</summary>
    public static bool IsUserError(Exception e) => e is RadioException or InvalidDataException or UnauthorizedAccessException;
}
