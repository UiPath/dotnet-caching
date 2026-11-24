using System.Buffers;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Registers the groups, caches and attestations of a key move move with <c>RouterBuilder.UseKeyMove</c>; every rule is checked when the router is built.</summary>
public sealed class KeyMoveBuilder
{
    // An evidence id is written into a comma- and colon-separated record, so neither may appear in it, nor whitespace.
    private static readonly SearchValues<char> EvidenceIdSeparators = SearchValues.Create(",: \t\r\n");
    private static readonly SearchValues<char> GlobCharacters = SearchValues.Create("*?[]\\");

    private readonly List<(string Name, bool Listed)> _groups = [];
    private readonly List<KeyMoveCacheRegistration> _caches = [];
    private readonly Dictionary<string, string> _formatSettled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeListComplete = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _memoryFits = new(StringComparer.Ordinal);
    private readonly List<string> _excluded = [];
    private readonly Dictionary<string, TimeSpan> _moveMinTtl = new(StringComparer.Ordinal);
    private string? _libraryPrefix;
    private bool _floorAttested;
    private Action<KeyMoveAlert>? _alert;

    internal KeyMoveBuilder()
    {
    }

    /// <summary>A group that is meant to move; <paramref name="listed"/> puts it in this node's <c>KeyMoveGroups</c>, so it has been Init-ed.</summary>
    public KeyMoveBuilder AddGroup(string name, bool listed = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _groups.Add((name, listed));
        return this;
    }

    /// <summary>A cache of a group, with its traits and key map.</summary>
    public KeyMoveBuilder AddCache(KeyMoveCacheRegistration cache)
    {
        _caches.Add(cache ?? throw new ArgumentNullException(nameof(cache)));
        return this;
    }

    /// <summary>The key prefix that starts every state, lease and control key of the move, so two deployments on one server never share them.</summary>
    public KeyMoveBuilder UseLibraryKeyPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        RefuseGlob(prefix);
        _libraryPrefix = prefix;
        return this;
    }

    /// <summary>A glob over the keys of caches the app uses directly, outside the router, which the key move never purges, counts or moves; no moving cache may have a pattern that can overlap it.</summary>
    public KeyMoveBuilder ExcludeKeyspace(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _excluded.Add(pattern);
        return this;
    }

    /// <summary>Sets the minimum remaining life of a key the mover moves in <paramref name="group"/>: a key with less life left is not moved and is counted as <c>short_ttl</c>, since a read promotes it when it is used and it expires on its own when it is not. A key with no expiry is never short.</summary>
    /// <remarks>The default is 10 minutes. A cache can override it with <see cref="KeyMoveCacheRegistration.MoveMinTtl"/>, and a <see cref="KeyMoveCacheRegistration.NoOrigin"/> cache ignores it. A read-through promotion ignores it too.</remarks>
    /// <param name="group">The group, which must be added with <see cref="AddGroup"/>.</param>
    /// <param name="minTtl">The minimum remaining life; <see cref="TimeSpan.Zero"/> moves every key.</param>
    public KeyMoveBuilder UseMoveMinTtl(string group, TimeSpan minTtl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentOutOfRangeException.ThrowIfLessThan(minTtl, TimeSpan.Zero);
        _moveMinTtl[group] = minTtl;
        return this;
    }

    /// <summary>States in configuration that no build below the first key move binary is deployed.</summary>
    public KeyMoveBuilder AttestReleaseFloor()
    {
        _floorAttested = true;
        return this;
    }

    /// <summary>States that no format switch is under way for <paramref name="group"/>, with the id of the evidence for it.</summary>
    /// <remarks>An id is good for one move, including its reverse: the first Start records it in the group's state, and no later Start, on any node or after a restart, accepts it again. A second move needs a new evidence id in the configuration, deployed to every node.</remarks>
    /// <param name="group">The group the evidence covers.</param>
    /// <param name="evidenceId">A label for the evidence, without comma, colon or whitespace; it is logged and stored with the move.</param>
    public KeyMoveBuilder AttestFormatSettled(string group, string evidenceId)
    {
        Attest(_formatSettled, group, evidenceId);
        return this;
    }

    /// <summary>States that the node list of <paramref name="group"/> is complete, with the id of the evidence for it.</summary>
    /// <remarks>An id is good for one move, including its reverse: the first Start records it in the group's state, and no later Start, on any node or after a restart, accepts it again. A second move needs a new evidence id in the configuration, deployed to every node.</remarks>
    /// <param name="group">The group the evidence covers.</param>
    /// <param name="evidenceId">A label for the evidence, without comma, colon or whitespace; it is logged and stored with the move.</param>
    public KeyMoveBuilder AttestNodeListComplete(string group, string evidenceId)
    {
        Attest(_nodeListComplete, group, evidenceId);
        return this;
    }

    /// <summary>States that the growth of the move of <paramref name="group"/> fits the memory plan, for one dry-run report only; a newer report needs a new attestation.</summary>
    /// <param name="group">The group the dry run covered.</param>
    /// <param name="dryRunReportId">The id of the accepted dry-run report: the Unix time in milliseconds, as decimal text, at which the dry run was stored in the <c>dry</c> field of the group's state. The operator takes it from that report; a new dry run has a new id.</param>
    public KeyMoveBuilder AttestMemoryFits(string group, string dryRunReportId)
    {
        Attest(_memoryFits, group, dryRunReportId);
        return this;
    }

    /// <summary>Told of every alert of the node and the leader.</summary>
    public KeyMoveBuilder OnAlert(Action<KeyMoveAlert> alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // A handler that throws must not end a loop of the node or abort a scan.
        _alert = a =>
        {
            try
            {
                alert(a);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceError($"A key move alert handler threw on '{a.Code}': {ex.Message}");
            }
        };
        return this;
    }

    internal KeyMoveSetup Build(IMigrationStageSource stages, IReadOnlyDictionary<string, (string Group, Func<string, string> InPlaceKey)> routed)
    {
        var prefix = _libraryPrefix ?? throw new InvalidOperationException("Key move needs UseLibraryKeyPrefix.");
        RefuseGlob(prefix);
        _ = new MoveKeyNames(string.Empty, prefix, false);
        if (_groups.Count == 0)
        {
            throw new InvalidOperationException("Key move has no group: add one with AddGroup.");
        }

        var groups = ValidateGroups();
        var caches = new List<KeyMoveCache>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cache in _caches)
        {
            MoveKeyNames.ValidateName(cache.Name, nameof(cache));
            if (!names.Add(cache.Name))
            {
                throw new InvalidOperationException($"The key move cache '{cache.Name}' is added twice; a cache belongs to one group and is registered once.");
            }

            caches.Add(Convert(cache, groups, routed, prefix));
        }

        ValidateRelationships(groups, caches, names, routed);
        RefuseExcludedOverlap(caches);
        RefuseSharedPatterns(caches);
        var registrations = _groups.Select(g => new GroupRegistration(g.Name, true, g.Listed, [.. caches.Where(c => c.Group == g.Name).Select(c => c.Name)])).ToArray();
        return new KeyMoveSetup(registrations, stages, new RegisteredGroupFacts(caches, _floorAttested, _formatSettled, _nodeListComplete, _memoryFits), new RegisteredKeySpace(caches, prefix, _alert, [.. _excluded], new Dictionary<string, TimeSpan>(_moveMinTtl)), new UnavailableMoveClaim(), prefix) { Alert = _alert };
    }

    // The prefix heads every scan pattern, so a character that a pattern reads as a wildcard would make the scan miss the keys it names.
    private static void RefuseGlob(string prefix)
    {
        if (prefix.AsSpan().IndexOfAny(GlobCharacters) >= 0)
        {
            throw new ArgumentException($"The library key prefix '{prefix}' holds a glob character (* ? [ ] \\), which a scan pattern would read as a wildcard.", nameof(prefix));
        }
    }

    private static void Attest(Dictionary<string, string> into, string group, string evidenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        if (evidenceId.AsSpan().IndexOfAny(EvidenceIdSeparators) >= 0)
        {
            throw new ArgumentException($"The evidence id '{evidenceId}' holds a comma, a colon or whitespace.", nameof(evidenceId));
        }

        if (!into.TryAdd(group, evidenceId))
        {
            throw new InvalidOperationException($"The group '{group}' is attested twice for the same fact.");
        }
    }

    private static KeyMoveCache Convert(KeyMoveCacheRegistration cache, HashSet<string> groups, IReadOnlyDictionary<string, (string Group, Func<string, string> InPlaceKey)> routed, string prefix)
    {
        MoveKeyNames.ValidateName(cache.Name, nameof(cache));
        if (cache.ParseInPlaceKey is null || cache.ParseNativeKey is null || cache.FormatNativeKey is null)
        {
            throw new InvalidOperationException($"The key move cache '{cache.Name}' needs ParseInPlaceKey, ParseNativeKey and FormatNativeKey.");
        }

        ValidateKeyspace(cache, prefix);
        if (cache.MoveMinTtl is { } minTtl && minTtl < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"The key move cache '{cache.Name}' has a MoveMinTtl that is negative.");
        }

        if (!groups.Contains(cache.Group))
        {
            throw new InvalidOperationException($"The key move cache '{cache.Name}' names the group '{cache.Group}', which was not added.");
        }

        if (!routed.TryGetValue(cache.Name, out var router) || router.Group != cache.Group)
        {
            throw new InvalidOperationException($"The key move cache '{cache.Name}' is not registered with the router in the group '{cache.Group}'.");
        }

        return new KeyMoveCache(cache.Name, cache.Group, cache.NoOrigin || cache.AtMostOnce, cache.AtMostOnce, cache.AcceptRefill, cache.MaxSourceTtl, cache.InPlacePattern, router.InPlaceKey, cache.ParseInPlaceKey, cache.NativePattern, cache.FormatNativeKey, cache.ParseNativeKey, cache.OwnsInPlaceKey, cache.OwnsNativeKey, cache.MoveMinTtl);
    }

    private static void ValidateKeyspace(KeyMoveCacheRegistration cache, string prefix)
    {
        if (string.IsNullOrWhiteSpace(cache.InPlacePattern) || string.IsNullOrWhiteSpace(cache.NativePattern))
        {
            throw new InvalidOperationException($"The key move cache '{cache.Name}' needs a scan pattern for each side.");
        }

        if (cache.InPlacePattern.AsSpan().IndexOfAny("*?[\\") == 0)
        {
            throw new InvalidOperationException($"The in-place pattern '{cache.InPlacePattern}' of the key move cache '{cache.Name}' starts with a wildcard, so it would cover the whole keyspace; it must start with the application's own key text.");
        }

        if (!cache.NativePattern.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The native pattern '{cache.NativePattern}' of the key move cache '{cache.Name}' is outside the application's namespace '{prefix}'.");
        }

        if (!UnderPrefix(cache, prefix))
        {
            throw new InvalidOperationException($"FormatNativeKey of the key move cache '{cache.Name}' makes a key outside the application's namespace '{prefix}'.");
        }

        if (cache.MaxSourceTtl is { } ttl && ttl <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"The key move cache '{cache.Name}' has a MaxSourceTtl that is not positive.");
        }
    }

    // A formatted key is checked on probes, since a function cannot be proved for every key; the key space checks every key it meets again.
    private static bool UnderPrefix(KeyMoveCacheRegistration cache, string prefix)
    {
        foreach (var probe in new[] { "k", "key:1", "0" })
        {
            string made;
            try
            {
                made = cache.FormatNativeKey(probe);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A function that does not take this probe (a numeric key, say) is judged on the keys it is given at use time.
                continue;
            }

            if (made is null || !made.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Two caches of a group that scan the same pattern can tell their keys apart only by a predicate of their own.
    private static void RefuseSharedPatterns(List<KeyMoveCache> caches)
    {
        foreach (var side in new[] { MoveSide.Legacy, MoveSide.Native })
        {
            var shared = caches.GroupBy(c => c.PatternOf(side)).FirstOrDefault(g => g.Count() > 1 && g.Any(c => (side == MoveSide.Native ? c.OwnsNative : c.OwnsInPlace) is null));
            if (shared is not null)
            {
                throw new InvalidOperationException($"The key move caches {string.Join(", ", shared.Select(c => $"'{c.Name}'"))} scan the same {(side == MoveSide.Native ? "native" : "in-place")} pattern '{shared.Key}'; each needs an ownership predicate that separates its keys from the others'.");
            }
        }
    }

    // A moving cache must keep clear of the keyspaces of the caches the app uses directly, by its scan patterns.
    private void RefuseExcludedOverlap(List<KeyMoveCache> caches)
    {
        foreach (var cache in caches)
        {
            foreach (var (side, pattern) in new[] { ("in-place", cache.InPlacePattern), ("native", cache.NativePattern) })
            {
                if (_excluded.FirstOrDefault(excluded => GlobPatterns.MayOverlap(excluded, pattern)) is { } clash)
                {
                    throw new InvalidOperationException($"The {side} pattern '{pattern}' of the key move cache '{cache.Name}' can overlap the excluded keyspace '{clash}' of a cache the app uses directly.");
                }
            }
        }
    }

    private HashSet<string> ValidateGroups()
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in _groups)
        {
            MoveKeyNames.ValidateName(name, "group");
            if (!groups.Add(name))
            {
                throw new InvalidOperationException($"The key move group '{name}' is added twice.");
            }
        }

        return groups;
    }

    private void ValidateRelationships(HashSet<string> groups, List<KeyMoveCache> caches, HashSet<string> names, IReadOnlyDictionary<string, (string Group, Func<string, string> InPlaceKey)> routed)
    {
        var named = _moveMinTtl.Keys.FirstOrDefault(g => !groups.Contains(g));
        if (named is not null)
        {
            throw new InvalidOperationException($"UseMoveMinTtl names the group '{named}', which was not added.");
        }

        var attested = new[] { _formatSettled, _nodeListComplete, _memoryFits }.SelectMany(d => d.Keys).FirstOrDefault(g => !groups.Contains(g));
        if (attested is not null)
        {
            throw new InvalidOperationException($"An attestation names the group '{attested}', which was not added.");
        }

        var orphan = routed.Where(r => groups.Contains(r.Value.Group) && !names.Contains(r.Key)).Select(r => r.Key).FirstOrDefault();
        if (orphan is not null)
        {
            throw new InvalidOperationException($"The router cache '{orphan}' belongs to the key move group '{routed[orphan].Group}' but is not registered with AddCache, so it would sit outside the key map and the managed set.");
        }

        var name = groups.FirstOrDefault(g => caches.All(c => c.Group != g));
        if (name is not null)
        {
            throw new InvalidOperationException($"The key move group '{name}' has no cache.");
        }
    }
}
