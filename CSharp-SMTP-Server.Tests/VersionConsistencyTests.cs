using System.Reflection;
using System.Xml.Linq;

namespace CSharp_SMTP_Server.Tests;

/// <summary>
/// Phase 1 (§9): meta tests that catch version drift between the source constants, the assembly
/// attributes and the NuGet package metadata. (Known current drift: VersionString says
/// "-krugertech.1" while PackageVersion is "1.1.6-krugertech.3" — only the numeric part is enforced.)
/// </summary>
public sealed class VersionConsistencyTests
{
    [Fact]
    public void VersionString_NumericPrefix_MatchesCsprojPackageVersion()
    {
        // BaseDirectory is <root>/CSharp-SMTP-Server.Tests/bin/<Config>/<tfm>/ — four levels up to the repo root.
        var csprojPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "CSharp-SMTP-Server", "CSharp-SMTP-Server.csproj");
        Assert.True(File.Exists(csprojPath), $"Library csproj not found at: {csprojPath}");

        var packageVersion = XDocument.Load(csprojPath)
            .Descendants()
            .First(e => e.Name.LocalName == "PackageVersion")
            .Value; // e.g. "1.1.6-krugertech.3"

        var packageNumeric = packageVersion.Split('-')[0];
        var versionStringNumeric = SMTPServer.VersionString.Split('-')[0];

        Assert.True(
            packageNumeric == versionStringNumeric,
            $"SMTPServer.VersionString ({SMTPServer.VersionString}) and PackageVersion ({packageVersion}) have diverged numerically.");
    }

    [Fact]
    public void AssemblyVersionString_IsFourPart_AndMatchesAssemblyAttribute()
    {
        var parts = SMTPServer.AssemblyVersionString.Split('.');
        Assert.Equal(4, parts.Length); // AssemblyVersion requires major.minor.build.revision

        var parsed = Version.Parse(SMTPServer.AssemblyVersionString);
        Assert.Equal(parsed, typeof(SMTPServer).Assembly.GetName().Version);
    }
}
