using System.Collections.Concurrent;

namespace UiPath.Caching.PhaseB;

/// <summary>How long the set of fresh nodes of a group has stayed the same, on the leader's monotonic clock: a node that joins or lapses restarts it.</summary>
internal sealed class ViewStability(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (string Signature, long Since)> _groups = new(StringComparer.Ordinal);

    public TimeSpan Observe(string group, IEnumerable<NodeAdvert> fresh)
    {
        var signature = string.Join('|', fresh.Select(a => a.Node).Order(StringComparer.Ordinal));
        var now = time.GetTimestamp();
        var held = _groups.AddOrUpdate(group, _ => (signature, now), (_, old) => old.Signature == signature ? old : (signature, now));
        return time.GetElapsedTime(held.Since, now);
    }
}
