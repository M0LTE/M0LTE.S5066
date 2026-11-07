using System.Text.Json.Nodes;

namespace M0LTE.S5066.Core.Tests.T2;

/// <summary>
/// The overlay merge of corpus-format.md section 6: objects merge key by key, recursively; any other
/// value, including a list or null, replaces the base value. A key not in the base, or an object
/// replaced by a non-object or the other way round, is an error.
/// </summary>
internal static class ConfigMerge
{
    public static JsonObject Merge(JsonObject baseConfig, JsonObject overlay) => Merge(baseConfig, overlay, "");

    private static JsonObject Merge(JsonObject baseConfig, JsonObject overlay, string path)
    {
        var result = (JsonObject)baseConfig.DeepClone();
        foreach (KeyValuePair<string, JsonNode?> kv in overlay)
        {
            string key = path + kv.Key;
            if (!baseConfig.ContainsKey(kv.Key))
            {
                throw new InvalidDataException($"config key '{key}' is not in defaults.json");
            }

            JsonNode? b = baseConfig[kv.Key];
            if (b is JsonObject bo && kv.Value is JsonObject vo)
            {
                result[kv.Key] = Merge(bo, vo, key + ".");
            }
            else if (b is JsonObject || kv.Value is JsonObject)
            {
                throw new InvalidDataException($"config key '{key}' mixes an object and a non-object");
            }
            else
            {
                result[kv.Key] = kv.Value?.DeepClone();
            }
        }

        return result;
    }
}
