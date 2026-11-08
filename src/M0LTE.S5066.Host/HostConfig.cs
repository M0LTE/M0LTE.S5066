using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host;

/// <summary>The modem a host runs (the <c>host.modem</c> object of a host configuration file).</summary>
/// <param name="Kind">"ms110d": the M0LTE.Ms110d adapter over UDP audio.</param>
/// <param name="AudioListen">Where UDP audio is received.</param>
/// <param name="AudioPeer">Where UDP audio is sent.</param>
public sealed record HostModemSettings(string Kind, IPEndPoint? AudioListen, IPEndPoint? AudioPeer);

/// <summary>
/// A host configuration file (core-architecture.md 12.9): the node keys of corpus-format.md
/// section 6, merged over the corpus <c>defaults.json</c>, plus a <c>host</c> object. <c>modem.*</c>
/// is ignored by a real modem and <c>time.*</c> is overwritten by the host (12.2).
/// </summary>
/// <param name="Node">The node configuration.</param>
/// <param name="PrngSeed">The PRNG seed: <c>prng_seed</c> when the file sets it, otherwise one drawn from the OS.</param>
/// <param name="SisEndpoint">host.sis_listen, default 127.0.0.1:5066 (S-0007, S-0008, S-0009).</param>
/// <param name="Modem">host.modem, or null when the file has none.</param>
public sealed record HostConfig(NodeConfig Node, ulong PrngSeed, IPEndPoint SisEndpoint, HostModemSettings? Modem)
{
    /// <summary>The default SIS endpoint: localhost, port 5066 (S-0007; plan 10.1).</summary>
    public static IPEndPoint DefaultSisEndpoint => new(IPAddress.Loopback, 5066);

    /// <summary>Loads a file.</summary>
    public static HostConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Parses the text of a host configuration file.</summary>
    /// <exception cref="ConfigException">A key is unknown or a value is wrong.</exception>
    public static HostConfig Parse(string json)
    {
        JsonObject file = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new ConfigException("(file)", "must be a JSON object");
        JsonObject? host = null;
        if (file["host"] is JsonObject h)
        {
            host = h;
            file.Remove("host");
        }

        bool seeded = file.ContainsKey("prng_seed");
        JsonObject merged = Merge(Defaults(), file, "");
        NodeConfig node = ConfigReader.Read(merged);
        ulong seed = seeded ? ConfigReader.ReadSeed(merged) : BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));

        IPEndPoint sis = DefaultSisEndpoint;
        HostModemSettings? modem = null;
        if (host is not null)
        {
            foreach (string key in host.Select(kv => kv.Key))
            {
                if (key is not ("sis_listen" or "modem"))
                {
                    throw new ConfigException("host." + key, "unknown key");
                }
            }

            if (host["sis_listen"] is JsonNode s)
            {
                sis = Endpoint(s, "host.sis_listen");
            }

            if (host["modem"] is JsonObject m)
            {
                string kind = m["kind"]?.GetValue<string>() ?? throw new ConfigException("host.modem.kind", "is required");
                if (kind != "ms110d")
                {
                    throw new ConfigException("host.modem.kind", "must be \"ms110d\"");
                }

                modem = new HostModemSettings(
                    kind,
                    m["audio_listen"] is JsonNode al ? Endpoint(al, "host.modem.audio_listen") : null,
                    m["audio_peer"] is JsonNode ap ? Endpoint(ap, "host.modem.audio_peer") : null);
            }
        }

        return new HostConfig(node, seed, sis, modem);
    }

    /// <summary>The corpus <c>defaults.json</c> this host was built with.</summary>
    public static JsonObject Defaults()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("M0LTE.S5066.Host.defaults.json")
            ?? throw new InvalidOperationException("defaults.json is not embedded");
        return (JsonObject)JsonNode.Parse(s)!;
    }

    /// <summary>
    /// The overlay merge of corpus-format.md section 6: objects merge key by key; any other value,
    /// a list or null included, replaces the base value. A key not in the base is an error.
    /// </summary>
    public static JsonObject Merge(JsonObject baseConfig, JsonObject overlay) => Merge(baseConfig, overlay, "");

    private static JsonObject Merge(JsonObject baseConfig, JsonObject overlay, string path)
    {
        var result = (JsonObject)baseConfig.DeepClone();
        foreach (KeyValuePair<string, JsonNode?> kv in overlay)
        {
            string key = path + kv.Key;
            if (!baseConfig.ContainsKey(kv.Key))
            {
                throw new ConfigException(key, "unknown key");
            }

            JsonNode? b = baseConfig[kv.Key];
            if (b is JsonObject bo && kv.Value is JsonObject vo)
            {
                result[kv.Key] = Merge(bo, vo, key + ".");
            }
            else if (b is JsonObject || kv.Value is JsonObject)
            {
                throw new ConfigException(key, "mixes an object and a non-object");
            }
            else
            {
                result[kv.Key] = kv.Value?.DeepClone();
            }
        }

        return result;
    }

    private static IPEndPoint Endpoint(JsonNode node, string key) =>
        IPEndPoint.TryParse(node.GetValue<string>(), out IPEndPoint? ep)
            ? ep
            : throw new ConfigException(key, "must be address:port, for example 127.0.0.1:5066");
}
