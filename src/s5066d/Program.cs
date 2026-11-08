using M0LTE.S5066.Core.Dts;
using M0LTE.S5066.Core.Runtime;
using M0LTE.S5066.Host;
using M0LTE.S5066.Host.Audio;
using M0LTE.S5066.Host.Modem;

// s5066d: one STANAG 5066 node (core-architecture.md 12.9). Usage: s5066d <config.json>
if (args.Length != 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: s5066d <config.json>");
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

if (config.Modem is not { AudioListen: { } listen, AudioPeer: { } peer })
{
    Console.Error.WriteLine("s5066d: host.modem needs kind \"ms110d\", audio_listen and audio_peer");
    return 1;
}

using var audio = new UdpAudioChannel(listen, peer);
await using var host = new NodeHost(config.Node, config.PrngSeed, new Ms110dHostModem(audio, log: line => Console.WriteLine(line)),
    new NodeHostOptions { SisEndpoint = config.SisEndpoint, Log = line => Console.WriteLine(line) });
host.Start();
Console.WriteLine($"s5066d: node {DpduAddress.ToDotted(config.Node.LocalAddress)}, MS110D over UDP audio {listen} -> {peer}");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await Task.WhenAny(stop.Task, host.Completion);
if (host.Completion.IsFaulted)
{
    Console.Error.WriteLine($"s5066d: the node stopped: {host.Completion.Exception!.InnerException?.Message}");
    return 1;
}

Console.WriteLine("s5066d: stopping");
return 0;
