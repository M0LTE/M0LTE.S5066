using System.Runtime.InteropServices;
using M0LTE.S5066.Client;
using M0LTE.S5066.Mail;

// s5066-mail: the CFTP mail gateway (docs/s5066-mail.md). Usage: s5066-mail <config.json>
if (args.Length != 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: s5066-mail <config.json>");
    Console.Error.WriteLine("  passes mail between an SMTP server and a STANAG 5066 node by CFTP (SAP 12)");
    return 2;
}

MailConfig config;
SmtpCredentials? credentials = null;
try
{
    config = MailConfig.Load(args[0]);
    if (config.Relay?.CredentialsFile is { } cf)
    {
        credentials = SmtpCredentials.Load(cf);
    }
}
catch (Exception e) when (e is MailConfigException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"s5066-mail: {args[0]}: {e.Message}");
    return 1;
}

void Log(string line) => Console.WriteLine(line);

MailGateway gateway;
try
{
    gateway = new MailGateway(config, Log, credentials: credentials);
    gateway.Start();
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
{
    Console.Error.WriteLine($"s5066-mail: {e.Message}");
    return 1;
}

await using (gateway)
{
    Log($"s5066-mail: node {config.SisHost}:{config.SisPort}, relay {config.Relay!.Host}:{config.Relay.Port}"
        + (config.Relay.StartTls ? " with STARTTLS" : "") + (credentials is null ? "" : " with AUTH")
        + (gateway.ListenEndpoint is { } ep ? $", SMTP in on {ep}" : ", no SMTP listener")
        + $", {config.Routes.Routes.Count} route(s), spool {config.Spool}");

    using var stop = new CancellationTokenSource();
    using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
    using PosixSignalRegistration intr = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
    await gateway.RunAsync(ct => SisClient.ConnectAsync(config.SisHost, config.SisPort, ct), stop.Token).ConfigureAwait(false);
    Log("s5066-mail: stopping");
}

return 0;
