using System.Globalization;

namespace UiPath.Caching.Migration;

/// <summary>What one <c>INFO</c> reply says about a node that the mover cares about.</summary>
/// <param name="Unit">The scan unit the node belongs to.</param>
/// <param name="IsReplica">Whether the node is a replica of the unit's primary.</param>
/// <param name="UsedMemory">Bytes of <c>used_memory</c>.</param>
/// <param name="MaxMemory">Bytes of <c>maxmemory</c>; zero means no limit.</param>
/// <param name="Policy">The <c>maxmemory_policy</c>.</param>
/// <param name="EvictedKeys">The running count of <c>evicted_keys</c>.</param>
/// <param name="CpuSeconds">The running <c>used_cpu_sys</c> plus <c>used_cpu_user</c>.</param>
/// <param name="ReplOffset">The replication offset: <c>master_repl_offset</c> on a primary, <c>slave_repl_offset</c> on a replica.</param>
/// <param name="LinkUp">On a replica, whether <c>master_link_status</c> is <c>up</c>; null on a primary.</param>
/// <param name="Node">The replica's identity within its unit; empty for a primary.</param>
internal sealed record NodeInfo(string Unit, bool IsReplica, long UsedMemory, long MaxMemory, string Policy, long EvictedKeys, double CpuSeconds, long? ReplOffset, bool? LinkUp, string Node = "")
{
    /// <summary>A replica the topology names that the connection cannot reach: no reading, and its link counts as down.</summary>
    public static NodeInfo Down(string unit, string node) => new(unit, true, 0, 0, string.Empty, 0, 0, null, false, node);

    /// <summary>Reads an <c>INFO</c> reply; a required field that is missing or unparseable throws <see cref="FormatException"/>.</summary>
    public static NodeInfo Parse(string unit, bool isReplica, string text, string node = "")
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (trimmed.Length == 0 || trimmed[0] == '#' || colon <= 0)
            {
                continue;
            }

            fields[trimmed[..colon]] = trimmed[(colon + 1)..];
        }

        return FromFields(unit, isReplica, fields, node);
    }

    private static NodeInfo FromFields(string unit, bool isReplica, Dictionary<string, string> fields, string node)
    {
        string Text(string name) => fields.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new FormatException($"INFO of {unit} has no {name}.");
        long Long(string name) => long.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : throw new FormatException($"INFO of {unit} has an unreadable {name}.");
        double Double(string name) => double.TryParse(Text(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : throw new FormatException($"INFO of {unit} has an unreadable {name}.");
        var offset = isReplica ? "slave_repl_offset" : "master_repl_offset";
        bool? link = isReplica ? string.Equals(Text("master_link_status"), "up", StringComparison.Ordinal) : null;

        // The lag is judged from both offsets, so each is required; the one of a replica whose link is down is not, since that replica reads red on the link alone.
        long? replOffset = link == false ? OptionalOffset(fields, offset) : Long(offset);
        return new NodeInfo(
            unit,
            isReplica,
            Long("used_memory"),
            Long("maxmemory"),
            Text("maxmemory_policy"),
            Long("evicted_keys"),
            Double("used_cpu_sys") + Double("used_cpu_user"),
            replOffset,
            link,
            node);
    }

    private static long? OptionalOffset(Dictionary<string, string> fields, string offset) =>
        fields.TryGetValue(offset, out var raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : null;
}
