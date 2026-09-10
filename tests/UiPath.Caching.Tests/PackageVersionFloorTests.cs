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

    /// <summary>
    /// A floor only reaches a consumer through a shipped project, so that is what this checks: every
    /// Microsoft.Extensions.* package a src project references has to be floored per TFM. The one that
    /// cannot be is Logging.Abstractions, because StackExchange.Redis 3.x asks for >= 10.0.5 on every
    /// target, net8.0 included, and an 8.0.x floor for it fails restore. Anything else escaping the
    /// floors is a floor net8.0 consumers lose, and should be argued for here rather than land quietly.
    /// Packages only samples or tests reference are not in the published dependency groups at all.
    /// </summary>
    [Fact]
    public void EveryShippedExtensionsPackageIsFlooredExceptLoggingAbstractions()
    {
        var floored = FloorGroup("net8.0").Select(e => PackageOf(e).Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shipped = ShippedPackageReferences()
            .Where(id => id.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
            .Where(id => !id.Equals("Microsoft.Extensions.Logging.Abstractions", StringComparison.Ordinal))
            .Where(id => !floored.Contains(id))
            .Order()
            .ToArray();

        shipped.Should().BeEmpty(
            "a Microsoft.Extensions.* package that a shipped project references without a per-TFM floor resolves 10.x for net8.0 consumers too, which is what the floors exist to avoid; Logging.Abstractions is the documented exception, forced by StackExchange.Redis 3.x asking for 10.0.5 or later on every target");
    }

    private static IEnumerable<string> ShippedPackageReferences()
    {
        var src = new DirectoryInfo(Path.Combine(RepositoryRoot().FullName, "src"));
        src.Exists.Should().BeTrue("the shipped projects live under src");

        return src.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .SelectMany(f => XDocument.Load(f.FullName).Descendants("PackageReference"))
            .Select(e => e.Attribute("Include")?.Value)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheNet10FloorIsDeclaredFirst()
    {
        var groups = Root().Elements("ItemGroup").ToList();
        var net10 = groups.FindIndex(IsFloor("net10.0"));
        var net8 = groups.FindIndex(IsFloor("net8.0"));

        // Both have to be found first: a missing net10 group would leave -1, which is less than any
        // index and would pass the order check while there is no floor to order.
        net10.Should().BeGreaterThanOrEqualTo(0, "Directory.Packages.props should declare a floor gated on 'net10.0'");
        net8.Should().BeGreaterThanOrEqualTo(0, "Directory.Packages.props should declare a floor gated on 'net8.0'");
        net10.Should().BeLessThan(net8,
            "Dependabot edits the first PackageVersion line it finds for an ID, so the net10 floor has to be the one it lands on rather than the net8 one");
    }

    [Fact]
    public void TheNet10FloorIsPinnedThroughOneProperty()
    {
        FloorGroup("net10.0").Select(e => e.Attribute("Version")!.Value).Should().AllBe("$(MEVersion10)",
            "the net10 family ships in lockstep, so a bump should have one line to change and no way to leave the group disagreeing");
    }

    private static Predicate<XElement> IsFloor(string tfm) =>
        g => g.Attribute("Condition")?.Value.Contains($"'{tfm}'", StringComparison.Ordinal) == true;

    private static IEnumerable<XElement> FloorGroup(string tfm)
    {
        var group = Root().Elements("ItemGroup")
            .SingleOrDefault(g => g.Attribute("Condition")?.Value.Contains($"'{tfm}'", StringComparison.Ordinal) == true);

        group.Should().NotBeNull($"Directory.Packages.props should declare exactly one ItemGroup gated on '{tfm}'");
        return group!.Elements("PackageVersion");
    }

    private static (string Id, string Version) PackageOf(XElement element) =>
        (element.Attribute("Include")!.Value, Resolve(element.Attribute("Version")!.Value));

    /// <summary>A floor pinned through a property gives a bump one line to land on for the whole family.</summary>
    private static string Resolve(string version)
    {
        if (!version.StartsWith("$(", StringComparison.Ordinal) || !version.EndsWith(')'))
        {
            return version;
        }

        var name = version[2..^1];
        var value = Root().Elements("PropertyGroup").Elements(name).LastOrDefault()?.Value;
        value.Should().NotBeNull($"'{version}' should resolve to a property declared in Directory.Packages.props");
        return value!;
    }

    private static XElement Root() =>
        XDocument.Load(Path.Combine(RepositoryRoot().FullName, "Directory.Packages.props")).Root!;

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("Directory.Packages.props should be findable by walking up from the test output directory");
        return directory!;
    }
}
