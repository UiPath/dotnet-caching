using System.Collections.Concurrent;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Keeps the rules of a group for a moment, so a hot path asks the snapshot monitor (which builds a view each time) a few times a second, not per call.</summary>
internal sealed class RulesMemo(TimeProvider clock, TimeSpan life)
{
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly long _lifeTicks = (long)(life.TotalSeconds * clock.TimestampFrequency);

    public GroupRules Get(string group, Func<string, GroupRules> read)
    {
        var now = clock.GetTimestamp();
        if (_held.TryGetValue(group, out var held) && now - held.At < _lifeTicks)
        {
            return held.Rules;
        }

        var rules = read(group);
        _held[group] = new Held(rules, now);
        return rules;
    }

    private sealed record Held(GroupRules Rules, long At);
}
