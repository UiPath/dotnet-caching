using System.Collections.Concurrent;

namespace UiPath.Caching.PhaseB;

/// <summary>The leader's reading of the <c>fl</c> views on its monotonic clock: how long a view stayed below the stage and how long every view stayed at a level.</summary>
internal sealed class FlagViews(StageDriverOptions options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, GroupSamples> _groups = new(StringComparer.Ordinal);

    /// <param name="stageFlag">The flag value the current state stands for: 2 for S0, 3 for S1 to S3, 4 for S4, 5 for S5.</param>
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
                samples.Nodes[node] = Advance(samples.Nodes.GetValueOrDefault(node), view, stageFlag, now);
            }

            var missing = views.Any(v => v.View is null);
            var known = views.Where(v => v.View is not null).Select(v => v.View!.Value).ToArray();
            var lowered = Lowered(samples, stageFlag, now);
            var confirmed = Confirmed(samples, views.Select(v => v.View).ToArray(), confirmLevel, now);
            return new FlagVerdict(views.Length, missing, known.Length == 0 ? null : known.Min(), lowered, confirmed);
        }
    }

    private static NodeSample Advance(NodeSample? previous, int? view, int stageFlag, long now)
    {
        var below = view is { } v && v < stageFlag;
        var belowSince = below ? previous?.BelowSince ?? now : (long?)null;
        var valueSince = previous is not null && previous.View == view ? previous.ValueSince : now;
        return new NodeSample(view, belowSince, valueSince);
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

    private TimeSpan Confirmed(GroupSamples samples, int?[] views, int? level, long now)
    {
        if (samples.ConfirmLevel != level)
        {
            samples.ConfirmSince = null;
            samples.ConfirmLevel = level;
        }

        if (level is not { } wanted || views.Length == 0 || views.Any(v => v is null || v < wanted))
        {
            samples.ConfirmSince = null;
            return TimeSpan.Zero;
        }

        samples.ConfirmSince ??= now;
        return time.GetElapsedTime(samples.ConfirmSince.Value, now);
    }

    private sealed record NodeSample(int? View, long? BelowSince, long ValueSince);

    private sealed class GroupSamples
    {
        public Dictionary<string, NodeSample> Nodes { get; } = new(StringComparer.Ordinal);

        public int StageFlag { get; set; } = -1;

        public long? ConfirmSince { get; set; }

        public int? ConfirmLevel { get; set; }
    }
}
