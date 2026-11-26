using System.Text.RegularExpressions;
using UiPath.Caching.PluginContract.Tests.Acme;

namespace UiPath.Caching.PluginContract.Tests;

public partial class FormatIdTableTests
{
    [Fact]
    public void The_fake_plugin_uses_no_id_the_allocation_table_gives_to_anyone()
    {
        var rows = TableRows();

        rows.Should().NotBeEmpty("the table lists at least the first-party ids");
        rows.Where(r => r.Kind == "Value").Select(r => r.Id).Should().NotIntersectWith(AcmeFormats.ValueIds);
        rows.Where(r => r.Kind == "Event").Select(r => r.Id).Should().NotContain(AcmeFormats.Event);
    }

    [GeneratedRegex(@"^\|\s*`0x(?<id>[0-9A-Fa-f]+)`\s*\|\s*(?<kind>Value|Event)\s*\|\s*`[^`]+`\s*\|\s*`[^`]+`\s*\|\s*$")]
    private static partial Regex Row();

    private static List<(string Kind, int Id)> TableRows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UiPath.Caching.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test runs inside the repository");
        var lines = File.ReadAllLines(Path.Combine(directory!.FullName, "docs", "reference", "format-ids.md"));
        var start = Array.FindIndex(lines, l => l.StartsWith("## Allocation", StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0);
        var table = lines.Skip(start).Where(l => l.StartsWith("| `0x", StringComparison.Ordinal)).ToList();
        table.Where(l => !Row().IsMatch(l)).Should().BeEmpty("a row this test cannot parse would hide an allocated id");
        return [.. table
            .Select(l => Row().Match(l))
            .Select(m => (m.Groups["kind"].Value, Convert.ToInt32(m.Groups["id"].Value, 16))),
        ];
    }
}
