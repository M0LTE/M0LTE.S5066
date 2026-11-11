using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Sis;

namespace M0LTE.S5066.Mail;

/// <summary>A configuration the gateway refuses; the message names the key.</summary>
internal sealed class MailConfigException(string message) : Exception(message);

/// <summary>The SMTP relay that mail from HF goes to (V-0069).</summary>
/// <param name="Host">Host name or address.</param>
/// <param name="Port">TCP port.</param>
/// <param name="StartTls">True to require STARTTLS before anything else is sent.</param>
/// <param name="CredentialsFile">A file holding the user name on its first line and the password on its second, or null for no AUTH.</param>
internal sealed record RelayConfig(string Host, int Port, bool StartTls, string? CredentialsFile);

/// <summary>The s5066-mail configuration file, JSON with // comments.</summary>
internal sealed record MailConfig
{
    public string SisHost { get; init; } = "127.0.0.1";

    public int SisPort { get; init; } = 5066;

    /// <summary>Where mail waits; it must survive a restart.</summary>
    public string Spool { get; init; } = "/var/lib/s5066-mail";

    /// <summary>The name this gateway gives in its SMTP greeting, its EHLO and its bounces.</summary>
    public string Hostname { get; init; } = Dns.GetHostName();

    /// <summary>Where the SMTP listener takes mail for HF, or null for none.</summary>
    public IPEndPoint? Listen { get; init; } = new(IPAddress.Loopback, 2525);

    public RelayConfig? Relay { get; init; }

    public RouteTable Routes { get; init; } = new([]);

    public static MailConfig Load(string path) => Parse(File.ReadAllText(path));

    public static MailConfig Parse(string json)
    {
        JsonObject o = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new MailConfigException("the file is not a JSON object");
        string[] known = ["sis", "spool", "hostname", "listen", "relay", "routes"];
        foreach (KeyValuePair<string, JsonNode?> kv in o)
        {
            if (!known.Contains(kv.Key))
            {
                throw new MailConfigException($"unknown key {kv.Key}");
            }
        }

        var c = new MailConfig();
        if (Str(o, "sis") is { } sis)
        {
            (string h, int p) = HostPort(sis, "sis");
            c = c with { SisHost = h, SisPort = p };
        }

        if (Str(o, "spool") is { } spool)
        {
            c = c with { Spool = spool.Length > 0 ? spool : throw new MailConfigException("spool: a directory") };
        }

        if (Str(o, "hostname") is { } hn)
        {
            if (hn.Length == 0 || !hn.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-'))
            {
                throw new MailConfigException($"hostname: letters, digits, dots and hyphens: {hn}");
            }

            c = c with { Hostname = hn };
        }

        if (o.ContainsKey("listen"))
        {
            if (o["listen"] is null)
            {
                c = c with { Listen = null };
            }
            else
            {
                (string h, int p) = HostPort(Str(o, "listen")!, "listen");
                c = c with { Listen = IPAddress.TryParse(h, out IPAddress? a) ? new IPEndPoint(a, p) : throw new MailConfigException($"listen: an IP address and port: {h}") };
            }
        }

        if (o["relay"] is JsonObject r)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in r)
            {
                if (kv.Key is not ("host" or "port" or "starttls" or "credentials"))
                {
                    throw new MailConfigException($"relay: unknown key {kv.Key}");
                }
            }

            string host = Str(r, "host") is { Length: > 0 } h ? h : throw new MailConfigException("relay: needs host");
            int port = r["port"] switch
            {
                null => 25,
                JsonValue v when v.TryGetValue(out int i) && i is >= 1 and <= 65535 => i,
                _ => throw new MailConfigException("relay.port: a whole number 1 to 65535"),
            };
            bool tls = r["starttls"] switch
            {
                null => false,
                JsonValue v when v.TryGetValue(out bool b) => b,
                _ => throw new MailConfigException("relay.starttls: true or false"),
            };
            c = c with { Relay = new RelayConfig(host, port, tls, Str(r, "credentials")) };
        }
        else if (o["relay"] is not null)
        {
            throw new MailConfigException("relay: an object with host, port, starttls and credentials");
        }
        else
        {
            throw new MailConfigException("relay: needed; mail from HF goes to this SMTP server (V-0069)");
        }

        var routes = new List<MailRoute>();
        if (o["routes"] is JsonArray ra)
        {
            foreach (JsonNode? n in ra)
            {
                if (n is not JsonObject e || Str(e, "match") is not { } m || Str(e, "node") is not { } node)
                {
                    throw new MailConfigException("routes: each entry needs match and node");
                }

                routes.Add(new MailRoute(RouteTable.ParsePattern(m), Address(node, "routes.node")));
            }
        }
        else if (o["routes"] is not null)
        {
            throw new MailConfigException("routes: a list");
        }

        return c with { Routes = new RouteTable(routes) };
    }

    private static (string Host, int Port) HostPort(string s, string key)
    {
        int colon = s.LastIndexOf(':');
        if (colon < 1 || !int.TryParse(s[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 0 or > 65535)
        {
            throw new MailConfigException($"{key}: not host:port: {s}");
        }

        return (s[..colon].Trim('[', ']'), port);
    }

    private static string? Str(JsonObject o, string key) => o[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => throw new MailConfigException($"{key}: a string"),
    };

    /// <summary>Parses a node address "w.x.y.z" (w 0 to 15), as s5066-test and s5066-ip write them; mail goes to one node.</summary>
    internal static SisAddress Address(string text, string key) =>
        SisAddress.TryParse(text, false, 7, out SisAddress a)
            ? a
            : throw new MailConfigException($"{key}: not a 5066 node address (w.x.y.z): {text}");
}
