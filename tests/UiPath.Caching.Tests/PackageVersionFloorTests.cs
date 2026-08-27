using System.Xml.Linq;

namespace UiPath.Caching.Tests;

/// <summary>
/// Guards the per-TFM dependency floors in Directory.Packages.props. Dependabot does not evaluate the
/// $(TargetFramework) conditions, so a bump can land on the wrong floor while restore still succeeds.
/// </summary>
public class PackageVersionFloorTests
{
    [Theory]
    [InlineData("net8.0", 8)]
    [InlineData("net10.0", 10)]
    public void FloorGroupStaysOnItsOwnMajor(string tfm, int expectedMajor)
    {
        var offenders = FloorGroup(tfm)
            .Select(PackageOf)
            .Where(p => !p.Version.StartsWith($"{expectedMajor}.", StringComparison.Ordinal))
            .Select(p => $"{p.Id} = {p.Version}")
            .ToArray();

        offenders.Should().BeEmpty(
            $"the '{tfm}' ItemGroup is the dependency floor for {tfm} consumers and must stay on {expectedMajor}.x");
    }

    [Fact]
    public void FloorGroupsDeclareTheSamePackages()
    {
        var net8 = FloorGroup("net8.0").Select(e => PackageOf(e).Id).Order().ToArray();
        var net10 = FloorGroup("net10.0").Select(e => PackageOf(e).Id).Order().ToArray();

        net8.Should().Equal(net10, "both floors must declare the same package IDs so neither TFM loses a pin");
    }

    [Fact]
    public void FloorPackagesAreNotAlsoPinnedUnconditionally()
    {
        var floored = FloorGroup("net8.0").Concat(FloorGroup("net10.0"))
            .Select(e => PackageOf(e).Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unconditional = Root().Elements("ItemGroup")
            .Where(g => g.Attribute("Condition") is null)
            .SelectMany(g => g.Elements("PackageVersion"))
            .Select(e => PackageOf(e).Id)
            .Where(floored.Contains)
            .ToArray();

        unconditional.Should().BeEmpty(
            "a package pinned both conditionally and unconditionally is ambiguous, which is what lets a bump land on the wrong line");
    }

    private static IEnumerable<XElement> FloorGroup(string tfm)
    {
        var group = Root().Elements("ItemGroup")
            .SingleOrDefault(g => g.Attribute("Condition")?.Value.Contains($"'{tfm}'", StringComparison.Ordinal) == true);

        group.Should().NotBeNull($"Directory.Packages.props should declare exactly one ItemGroup gated on '{tfm}'");
        return group!.Elements("PackageVersion");
    }

    private static (string Id, string Version) PackageOf(XElement element) =>
        (element.Attribute("Include")!.Value, element.Attribute("Version")!.Value);

    private static XElement Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("Directory.Packages.props should be findable by walking up from the test output directory");
        return XDocument.Load(Path.Combine(directory!.FullName, "Directory.Packages.props")).Root!;
    }
}
