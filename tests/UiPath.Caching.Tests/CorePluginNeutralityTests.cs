using System.Reflection;
using System.Text.RegularExpressions;
using UiPath.Caching.MessagePack;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Tests;

public partial class CorePluginNeutralityTests
{
    private const string AllowedLine = "InternalsVisibleTo(\"UiPath.Caching.MessagePack\")";

    private static readonly string[] Names = ["MessagePack", "Lz4"];

    private static readonly string MessagePackOwner = typeof(MessagePackFormats).Assembly.GetName().Name!;

    [Fact]
    public void The_core_sources_do_not_name_a_serialization_plugin()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var project in new[] { "UiPath.Caching", "UiPath.Caching.Abstractions" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/"))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var isAllowed = relative.EndsWith("/GlobalUsings.cs", StringComparison.Ordinal) && lines[i].Contains(AllowedLine, StringComparison.Ordinal);
                    if (!isAllowed && Names.Any(n => lines[i].Contains(n, StringComparison.OrdinalIgnoreCase)))
                    {
                        offenders.Add($"{relative}:{i + 1}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void The_allocation_table_matches_the_message_pack_ids()
    {
        var rows = TableRows().Where(r => r.Owner == MessagePackOwner).ToList();
        var constants = typeof(MessagePackFormats).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => $"{nameof(MessagePackFormats)}.{f.Name}", f => (int)f.GetRawConstantValue()!);

        rows.Select(r => r.Name).Should().BeEquivalentTo(constants.Keys);
        foreach (var (_, _, name, id) in rows)
        {
            id.Should().Be(constants[name], name);
        }

        rows.Where(r => r.Kind == "Value").Select(r => r.Id).Should().BeEquivalentTo(MessagePackFormats.ValueIds);
    }

    [Fact]
    public void Every_table_row_is_an_allocated_id_once_per_kind()
    {
        var rows = TableRows();

        // Rows are the allocated one-byte ids or the published two-byte range; an event id is always one byte.
        rows.Should().OnlyContain(r => FormatIdRules.IsAllowed(r.Id) && (r.Id <= 0x0F || (r.Id >= 0x100 && r.Id <= 0x17F)));
        rows.Where(r => r.Kind == "Event").Should().OnlyContain(r => r.Id <= 0x0F);
        rows.GroupBy(r => (r.Kind, r.Id)).Should().OnlyContain(g => g.Count() == 1);
    }

    [GeneratedRegex(@"^\|\s*`0x(?<id>[0-9A-Fa-f]+)`\s*\|\s*(?<kind>\w+)\s*\|\s*`(?<owner>[^`]+)`\s*\|\s*`(?<name>[^`]+)`\s*\|\s*$")]
    private static partial Regex Row();

    private static List<(string Kind, string Owner, string Name, int Id)> TableRows()
    {
        var text = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "reference", "format-ids.md"));
        var start = Array.FindIndex(text, l => l.StartsWith("## Allocation", StringComparison.Ordinal));
        start.Should().BeGreaterThanOrEqualTo(0, "the table sits under an Allocation heading");
        return text.Skip(start)
            .Select(l => Row().Match(l))
            .Where(m => m.Success)
            .Select(m => (m.Groups["kind"].Value, m.Groups["owner"].Value, m.Groups["name"].Value, Convert.ToInt32(m.Groups["id"].Value, 16)))
            .ToList();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("Directory.Packages.props should be findable by walking up from the test output directory");
        return directory!.FullName;
    }
}
