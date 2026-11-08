using System.Xml.Linq;

namespace M0LTE.S5066.Radio.Tests;

/// <summary>
/// The pdn-soundmodem package is AGPL-3.0-or-later AND GPL-3.0-or-later AND GPL-3.0-only, so
/// nothing that is packed (and offered under the commercial licence too) may reach it: only the
/// unpacked radio project and s5066d (core-architecture.md 12.11).
/// </summary>
public class LicensingTests
{
    private const string Combined = "AGPL-3.0-or-later AND GPL-3.0-or-later AND GPL-3.0-only";

    [Fact]
    public void No_packed_project_references_the_radio_project_or_pdn_soundmodem()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(Root(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            XDocument project = XDocument.Load(path);
            bool reaches = project.Descendants("ProjectReference").Any(r => ((string?)r.Attribute("Include") ?? "").Contains("M0LTE.S5066.Radio", StringComparison.Ordinal))
                || project.Descendants("PackageReference").Any(r => (string?)r.Attribute("Include") == "pdn-soundmodem");
            if (reaches)
            {
                project.Descendants("IsPackable").Select(e => e.Value).Should().ContainSingle()
                    .Which.Should().Be("false", $"{Path.GetFileName(path)} links pdn-soundmodem, whose licence the commercial offer cannot cover");
                project.Descendants("PackageLicenseExpression").Select(e => e.Value).Should().ContainSingle()
                    .Which.Should().Be(Combined, $"{Path.GetFileName(path)} declares the licence it is under");
            }
        }
    }

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "M0LTE.S5066.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("the repository root was not found");
    }
}
