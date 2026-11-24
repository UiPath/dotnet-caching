namespace UiPath.Caching.Migration;

/// <summary>The memory rules over the primaries' readings: a ceiling, a growth limit over a window, and a pause for evictions.</summary>
internal sealed class MemoryRules
{
    /// <summary>The pause reasons, strongest first.</summary>
    public static readonly string[] Reasons = ["noeviction", "memory_ceiling", "evicted", "memory_growth"];

    private readonly Dictionary<string, long> _evicted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(DateTimeOffset At, long Used)>> _history = new(StringComparer.Ordinal);

    /// <summary>The reason of the strongest rule that holds over the primaries in <paramref name="nodes"/>, or null. Call it once for each reading, in order.</summary>
    public string? Evaluate(IEnumerable<NodeInfo> nodes, DateTimeOffset now, KeyMoveMoverOptions options)
    {
        var worst = int.MaxValue;
        foreach (var node in nodes.Where(n => !n.IsReplica))
        {
            var hit = EvaluateNode(node, now, options);
            if (hit is { } reason)
            {
                worst = Math.Min(worst, Array.IndexOf(Reasons, reason));
            }
        }

        return worst == int.MaxValue ? null : Reasons[worst];
    }

    private string? EvaluateNode(NodeInfo node, DateTimeOffset now, KeyMoveMoverOptions options)
    {
        string? hit = null;
        if (node.MaxMemory > 0 && node.UsedMemory >= options.MemoryCeilingFraction * node.MaxMemory)
        {
            hit = string.Equals(node.Policy, "noeviction", StringComparison.Ordinal) ? "noeviction" : "memory_ceiling";
        }

        // Any eviction since the last reading; the first reading of a node (or a counter that went back, after a restart) is only a baseline.
        if (_evicted.TryGetValue(node.Unit, out var before) && node.EvictedKeys > before)
        {
            hit ??= "evicted";
        }

        _evicted[node.Unit] = node.EvictedKeys;
        if (!_history.TryGetValue(node.Unit, out var history))
        {
            _history[node.Unit] = history = [];
        }

        history.Add((now, node.UsedMemory));
        history.RemoveAll(h => now - h.At > options.MemoryGrowthWindow);
        if (options.MaxMemoryGrowthBytesPerSecond > 0 && history.Count >= 2)
        {
            var (oldAt, oldUsed) = history[0];
            var seconds = (now - oldAt).TotalSeconds;
            if (seconds > 0 && (node.UsedMemory - oldUsed) / seconds > options.MaxMemoryGrowthBytesPerSecond)
            {
                hit ??= "memory_growth";
            }
        }

        return hit;
    }
}
