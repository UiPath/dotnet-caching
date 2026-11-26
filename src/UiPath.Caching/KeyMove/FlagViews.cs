using System.Collections.Concurrent;

namespace UiPath.Caching.KeyMove;

/// <summary>The leader's reading of the <c>fl</c> views on its monotonic clock: how long a view stayed below the stage and how long every view stayed at a level.</summary>
internal sealed class FlagViews(StageDriverOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, GroupSamples> _groups = new(StringComparer.Ordinal);

    /// <param name="stageFlag">The flag value the current state stands for: 2 for not-started, 3 for ready to moving, 4 for drained, 5 for closed.</param>
    /// <param name="confirmLevel">The level whose continuous agreement is measured (the CloseConfirm level), or null.</param>
    public FlagVerdict Sample(string group, IReadOnlyList<NodeAdvert> fresh, int stageFlag, int? confirmLevel = null)
    {
        var samples = _groups.GetOrAdd(group, _ => new GroupSamples());
        var now = time.GetTimestamp();
        lock (samples)
        {
            if (samples.StageFlag != stageFlag)
            {
                samples.Nodes.Clear();
                samples.StageFlag = stageFlag;
            }

            var views = fresh.Select(a => (a.Node, View: a.FlagOf(group))).ToArray();
            var seen = views.Select(v => v.Node).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in samples.Nodes.Keys.Where(n => !seen.Contains(n)).ToArray())
            {
                samples.Nodes.Remove(gone);
            }

            foreach (var (node, view) in views)
            {
                samples.Nodes[node] = Advance(samples.Nodes.GetValueOrDefault(node), view, stageFlag, confirmLevel, now);
            }

            var missing = views.Any(v => v.View is null);
            var known = views.Where(v => v.View is not null).Select(v => v.View!.Value).ToArray();
            var lowered = Lowered(samples, stageFlag, now);
            var confirmed = Confirmed(samples, confirmLevel, now);
            return new FlagVerdict(views.Length, missing, known.Length == 0 ? null : known.Min(), lowered, confirmed);
        }
    }

    private static NodeSample Advance(NodeSample? previous, int? view, int stageFlag, int? confirmLevel, long now)
    {
        var below = view is { } v && v < stageFlag;
        var belowSince = below ? previous?.BelowSince ?? now : (long?)null;
        var valueSince = previous is not null && previous.View == view ? previous.ValueSince : now;
        long? levelSince = confirmLevel is { } level && view >= level ? previous?.LevelSince ?? now : null;
        return new NodeSample(view, belowSince, valueSince, levelSince);
    }

    // Some node must have stayed below the stage for the hold; the way back goes to the lowest view that persisted for the hold.
    private int? Lowered(GroupSamples samples, int stageFlag, long now)
    {
        var persisted = samples.Nodes.Values
            .Where(s => s.View is { } v && v < stageFlag && s.BelowSince is not null && time.GetElapsedTime(s.BelowSince.Value, now) >= options.FlagLowerHold && time.GetElapsedTime(s.ValueSince, now) >= options.FlagLowerHold)
            .Select(s => s.View!.Value)
            .ToArray();
        return persisted.Length == 0 ? null : persisted.Min();
    }

    // Confirmation is per pod: the least time any fresh pod has shown the level, so a pod that joins restarts it.
    private TimeSpan Confirmed(GroupSamples samples, int? level, long now)
    {
        if (level is null || samples.Nodes.Count == 0 || samples.Nodes.Values.Any(n => n.LevelSince is null))
        {
            return TimeSpan.Zero;
        }

        return samples.Nodes.Values.Min(n => time.GetElapsedTime(n.LevelSince!.Value, now));
    }

    private sealed record NodeSample(int? View, long? BelowSince, long ValueSince, long? LevelSince);

    private sealed class GroupSamples
    {
        public Dictionary<string, NodeSample> Nodes { get; } = new(StringComparer.Ordinal);

        public int StageFlag { get; set; } = -1;
    }
}
