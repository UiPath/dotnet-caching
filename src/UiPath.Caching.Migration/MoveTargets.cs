using System.Collections.Concurrent;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>The mover's targets of each group, filled as the router binds the client of each cache.</summary>
/// <remarks>A cache the application has not resolved yet has no target: the mover alerts <c>mover_no_target</c> for its keys and the inventory keeps the drain blocked.</remarks>
internal sealed class MoveTargets
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IMoveTarget>> _groups = new(StringComparer.Ordinal);

    /// <summary>The live targets of <paramref name="group"/>, which later bindings add to.</summary>
    public IReadOnlyDictionary<string, IMoveTarget> Of(string group) => Group(group);

    /// <summary>Adds the target of one cache, replacing the one a previous library start bound.</summary>
    public void Add(string group, IMoveTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Group(group)[target.CacheId] = target;
    }

    private ConcurrentDictionary<string, IMoveTarget> Group(string group) =>
        _groups.GetOrAdd(group, static _ => new ConcurrentDictionary<string, IMoveTarget>(StringComparer.Ordinal));
}
