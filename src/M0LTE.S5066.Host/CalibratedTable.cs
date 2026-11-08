using System.Reflection;
using System.Text.Json.Nodes;
using M0LTE.S5066.Core.Runtime;

namespace M0LTE.S5066.Host;

/// <summary>
/// A calibrated <c>drc.table</c> and the modem it was measured on (core-architecture.md 9, phase 7,
/// design shape item 6): s5066-conformance <c>calibration/&lt;modem&gt;-&lt;version&gt;/drc-table.json</c>,
/// whose <c>table</c> is the drc.table of corpus-format.md 6.1 and whose <c>source</c> names the
/// calibration and the modem. Data, not an operator setting: the host loads it and overwrites
/// <c>drc.table</c>, as it overwrites <c>time.*</c>, and refuses a table measured on another modem.
/// </summary>
/// <param name="Calibration">The calibration's directory name, for example <c>ms110d-cs-0.6.1</c>.</param>
/// <param name="ModemPackage">The modem package it was measured on, for example <c>M0LTE.Ms110d</c>.</param>
/// <param name="ModemVersion">That package's version, for example <c>0.6.1</c>.</param>
/// <param name="Table">The table.</param>
public sealed record CalibratedTable(string Calibration, string ModemPackage, string ModemVersion, DrcTable Table)
{
    /// <summary>
    /// The calibration this host was built with: the corpus's table for M0LTE.Ms110d
    /// (<c>calibration/ms110d-cs-0.6.1/drc-table.json</c>), embedded at build time.
    /// </summary>
    public static CalibratedTable Default { get; } = LoadEmbedded();

    /// <summary>Whether this table was measured on the given modem package and version.</summary>
    public bool Matches(string modemPackage, string modemVersion) =>
        string.Equals(ModemPackage, modemPackage, StringComparison.Ordinal)
        && string.Equals(ModemVersion, modemVersion, StringComparison.Ordinal);

    /// <summary>Parses the text of a <c>drc-table.json</c>.</summary>
    /// <exception cref="ConfigException">The file is not a calibrated table, or its table is wrong.</exception>
    public static CalibratedTable Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject file)
        {
            throw new ConfigException("(calibration)", "must be a JSON object");
        }

        if (file["source"] is not JsonObject source || source["modem"] is not JsonObject modem)
        {
            throw new ConfigException("source.modem", "is required");
        }

        if (file["table"] is not JsonObject table)
        {
            throw new ConfigException("table", "is required");
        }

        return new CalibratedTable(
            Text(source, "calibration", "source."),
            Text(modem, "package", "source.modem."),
            Text(modem, "version", "source.modem."),
            ConfigReader.ReadDrcTable(table));
    }

    private static string Text(JsonObject o, string key, string where) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0
            ? s
            : throw new ConfigException(where + key, "must be a non-empty string");

    private static CalibratedTable LoadEmbedded()
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("M0LTE.S5066.Host.drc-table.json")
            ?? throw new InvalidOperationException("drc-table.json is not embedded");
        using var reader = new StreamReader(s);
        return Parse(reader.ReadToEnd());
    }
}
