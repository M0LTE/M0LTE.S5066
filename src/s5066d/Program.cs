using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;
using M0LTE.S5066.Radio;

// s5066d: one STANAG 5066 node (core-architecture.md 12.9). Usage: s5066d [--trace] <config.json>
bool trace = args.Length == 2 && args[0] == "--trace";
if (trace)
{
    args = args[1..];
}

if (args.Length != 1 || args[0] is "-h" or "--help" or "--trace")
{
    Console.Error.WriteLine("usage: s5066d [--trace] <config.json>");
    Console.Error.WriteLine("  --trace  log every D_PDU, timer, state change and burst timing (issue #86)");
    return 2;
}

HostConfig config;
try
{
    config = HostConfig.Load(args[0]);
}
catch (Exception e) when (e is ConfigException or IOException or System.Text.Json.JsonException or InvalidOperationException)
{
    Console.Error.WriteLine($"s5066d: {args[0]}: {e.Message}");
    return 1;
}

Action<string>? traceSink = trace ? line => Console.WriteLine(line) : null;
void Log(string line) => Console.WriteLine(line);

IDisposable medium;
IHostModem modem;
string describe;
RadioMedium? radio = null;
switch (config.Modem)
{
    case { Radio: { } radioObject }:
        RadioSettings settings;
        try
        {
            settings = RadioSettings.Parse(radioObject);
            radio = RadioMedium.Open(settings, Log);
        }
        catch (Exception e) when (e is ConfigException or RadioException)
        {
            Console.Error.WriteLine($"s5066d: {args[0]}: {e.Message}");
            return 1;
        }

        (medium, modem) = (radio, radio.Modem);
        describe = $"MS110D on the radio: capture {settings.CaptureDevice}, playback {settings.PlaybackDevice} at {settings.SampleRate} Hz, "
            + $"PTT {settings.PttDevice} GPIO {settings.PttGpio}, txdelay {settings.TxDelayMs} ms, tail {settings.TxTailMs} ms, tx_level {settings.TxLevel:0.###}, "
            + $"busy detect {(settings.BusyDetect ? "on" : "off")}" + (settings.RxRecord is { } rec ? $", recording to {rec}" : "");
        break;

    case { AudioListen: { } listen, AudioPeer: { } peer }:
        var udp = new UdpAudioChannel(listen, peer);
        udp.Trace = traceSink is null ? null : line => traceSink($"trace {line}");
        (medium, modem) = (udp, new Ms110dHostModem(udp, log: Log));
        describe = $"MS110D over UDP audio {listen} -> {peer}";
        break;

    default:
        Console.Error.WriteLine("s5066d: host.modem needs kind \"ms110d\" and either audio_listen and audio_peer (UDP audio) or a radio object");
        return 1;
}

using IDisposable ownedMedium = medium;
NodeHost made;
try
{
    made = new NodeHost(config.Node, config.PrngSeed, modem,
        new NodeHostOptions { SisEndpoint = config.SisEndpoint, Log = Log, Trace = traceSink });
}
catch (ConfigException e)
{
    // drc.default_speed the modem cannot send (issue #83).
    Console.Error.WriteLine($"s5066d: {args[0]}: {e.Message}");
    return 1;
}

if (radio is not null)
{
    foreach (string line in RadioMedium.Warnings(radio.Settings, config.Node, modem))
    {
        Log(line);
    }
}

await using NodeHost host = made;
var stop = new TaskCompletionSource();
string? failure = null;
if (radio is not null)
{
    radio.Audio.Failed += e =>
    {
        failure = e.Message;
        stop.TrySetResult();
    };
}

host.Start();
Console.WriteLine($"s5066d: node {DpduAddress.ToDotted(config.Node.LocalAddress)}, {describe}");

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
// SIGTERM (systemctl stop) is handled here rather than by the runtime's default, which ends the
// process without running the disposals below: those unkey the radio, and a CM108 holds its PTT
// pin where it was last set.
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM,
    context =>
    {
        context.Cancel = true;
        stop.TrySetResult();
    });
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await Task.WhenAny(stop.Task, host.Completion);
if (host.Completion.IsFaulted)
{
    Console.Error.WriteLine($"s5066d: the node stopped: {host.Completion.Exception!.InnerException?.Message}");
    return 1;
}

if (failure is not null)
{
    Console.Error.WriteLine($"s5066d: the radio stopped: {failure}");
    return 1;
}

Console.WriteLine("s5066d: stopping");
return 0;
