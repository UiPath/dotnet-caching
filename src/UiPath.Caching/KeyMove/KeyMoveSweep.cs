using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The sweep of N for one cache: every native key the cache owns, with its forward fence and its staging keys, unlinked in batches.</summary>
/// <remarks>
/// The scan covers the cache's own native patterns and, for the fences, the same patterns behind the <c>mg:</c> keyspace, through the connector prefix as
/// <see cref="MasterScanner"/> applies it. A key is unlinked only when the key space says this cache alone owns it (the data key a fence or staging key was made for),
/// so a cache whose name shares a prefix, a cache the app uses directly and a key of the move itself are never touched.
/// The reverse fences and staging keys name an in-place key by hash, so no per-cache pattern reaches them; they are short-lived and every publish through them is refused while the gate is closed.
/// </remarks>
/// <param name="database">The connection that adds the connector prefix to every key.</param>
internal sealed class KeyMoveSweep(MasterScanner scanner, IGroupKeySpace space, IDatabase database, MoveKeyNames names, int batchSize = 512)
{
    private const string FencePrefix = MoveKeyNames.Keyspace + ":";
    private const string StagingInfix = ":stg:";

    /// <summary>Unlinks what the cache owns in N and returns how many keys went; stops with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task<long> SweepAsync(string group, string cacheId, CancellationToken cancellationToken) => SweepAsync(group, cacheId, () => false, cancellationToken);

    /// <param name="lockLost">Asked before every batch is unlinked: a Clear whose lock has lapsed may have had its gates reopened, so no further key is deleted.</param>
    public async Task<long> SweepAsync(string group, string cacheId, Func<bool> lockLost, CancellationToken cancellationToken)
    {
        var data = space.PatternsOf(group, cacheId, MoveSide.Native);
        if (data.Count == 0)
        {
            throw new InvalidOperationException($"The cache '{cacheId}' has no native key pattern in the group '{group}', so its keys cannot be swept.");
        }

        var patterns = new List<string>(data);
        patterns.AddRange(data.Select(p => FencePrefix + p));

        // A pattern may end in literal text, so a staging key is scanned for by name instead of by what the pattern would let follow.
        patterns.AddRange(data.Select(p => p + StagingInfix + "*"));

        async Task<long> Pass(IAsyncEnumerable<string> keys)
        {
            long removed = 0;
            var batch = new List<string>(batchSize);
            await foreach (var key in keys.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Owned(group, cacheId, key))
                {
                    continue;
                }

                batch.Add(key);
                if (batch.Count >= batchSize)
                {
                    removed += await UnlinkAsync(batch, lockLost, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                removed += await UnlinkAsync(batch, lockLost, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }

        return await scanner.RunAsync(patterns, Pass, cancellationToken).ConfigureAwait(false);
    }

    // The data key of a forward fence or a staging key is judged, since the move's own names have no owner of their own.
    private bool Owned(string group, string cacheId, string key)
    {
        var subject = key;
        if (key.StartsWith(FencePrefix, StringComparison.Ordinal))
        {
            subject = key[FencePrefix.Length..];
        }
        else if (ReservedKeys.TryStagedKey(key, out var staged))
        {
            subject = staged;
        }

        var owners = space.OwnersOf(group, MoveSide.Native, subject);
        if (owners.Count > 1 && owners.Contains(cacheId))
        {
            throw new AmbiguousKeyException($"The key '{key}' is owned by {string.Join(" and ", owners)}, so the Clear of '{cacheId}' cannot tell whose it is.");
        }

        return owners is [var only] && only == cacheId;
    }

    private async Task<long> UnlinkAsync(List<string> batch, Func<bool> lockLost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lockLost())
        {
            throw new OperationCanceledException("The lock of the Clear lapsed, so no further batch is unlinked.");
        }


        // A cluster takes one slot per command; elsewhere one command carries the whole batch.
        var groups = names.Cluster ? batch.GroupBy(names.SlotOf).Select(g => g.ToArray()).ToArray() : [batch.ToArray()];
        var results = await Task.WhenAll(groups.Select(keys => database.ExecuteAsync("UNLINK", [.. keys.Select(k => (object)(RedisKey)k)], CommandFlags.DemandMaster))).ConfigureAwait(false);
        return results.Sum(r => (long)r);
    }
}
