using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using M0LTE.S5066.Client;
using M0LTE.S5066.Ip;

// s5066-ip: the Annex U IP client on a Linux tun device (docs/s5066-ip.md). Usage: s5066-ip <config.json>
if (args.Length != 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: s5066-ip <config.json>");
    Console.Error.WriteLine("  carries IP datagrams over a STANAG 5066 node (SAP 9) through a tun device; needs CAP_NET_ADMIN");
    return 2;
}

IpConfig config;
try
{
    config = IpConfig.Load(args[0]);
}
catch (Exception e) when (e is IpConfigException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"s5066-ip: {args[0]}: {e.Message}");
    return 1;
}

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("s5066-ip: needs Linux (a tun device)");
    return 1;
}

LinuxTun tun;
try
{
    tun = LinuxTun.Open(config.Device);
}
catch (IOException e)
{
    Console.Error.WriteLine($"s5066-ip: {e.Message}");
    return 1;
}

using (tun)
{
    void Log(string line) => Console.WriteLine(line);
    Log($"s5066-ip: {tun.Name} is up; node {config.SisHost}:{config.SisPort}, {config.Routes.Count} route(s)"
        + (config.DefaultNode is { } dn ? $", default node {IpConfig.Show(dn)}" : "")
        + (config.MulticastGroup is { } g ? $", multicast to {IpConfig.Show(g)}" : ", multicast dropped")
        + (config.Allow is null ? ", all traffic allowed" : $", {config.Allow.Count} allow entries"));

    using var stop = new CancellationTokenSource();
    using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
    using PosixSignalRegistration intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });

    await using var client = new IpClient(config, tun, Log);
    NotifyReady();
    Task run = client.RunAsync(ct => SisClient.ConnectAsync(config.SisHost, config.SisPort, ct), stop.Token);

    // The counters every ten minutes, when they have changed, and at the end.
    string last = "";
    while (!run.IsCompleted)
    {
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(10), stop.Token).ContinueWith(_ => { }, TaskScheduler.Default)).ConfigureAwait(false);
        string now = client.Counters.ToString();
        if (now != last && !run.IsCompleted)
        {
            Log($"s5066-ip: {now}");
            last = now;
        }
    }

    await run.ConfigureAwait(false);
    Log($"s5066-ip: stopping; {client.Counters}");
}

return 0;

// Tells systemd (Type=notify) the device exists, so ExecStartPost can add its addresses and routes.
static void NotifyReady()
{
    string? path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
    if (string.IsNullOrEmpty(path))
    {
        return;
    }

    if (path[0] == '@')
    {
        path = "\0" + path[1..];
    }

    try
    {
        using var s = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        s.SendTo(Encoding.ASCII.GetBytes("READY=1"), new UnixDomainSocketEndPoint(path));
    }
    catch (SocketException e)
    {
        Console.WriteLine($"s5066-ip: could not tell systemd it is ready: {e.Message}");
    }
}
