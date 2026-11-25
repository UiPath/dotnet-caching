using System.Runtime.CompilerServices;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>SCAN on every master through the server API, which does not apply the connector prefix: the pattern starts with it and each result has it stripped.</summary>
/// <remarks>Keys that overlapping patterns match are returned once, up to a bound. A scan needs every slot owned by a connected master and no change of ownership while it ran, otherwise it is repeated, and it fails when that never settles.</remarks>
internal sealed class MasterScanner(Func<IEnumerable<IServer>> servers, IStateTopology topology, int database, string connectorPrefix, TimeProvider time, TimeSpan retryDelay, int pageSize = 500, int attempts = 3, int maxDeduplicated = 1_000_000, Action<KeyMoveAlert>? alert = null)
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    /// <summary>Runs <paramref name="consume"/> over the keys of every master; its result is the one of the attempt in which the slot map did not change.</summary>
    /// <param name="consume">Called afresh for each attempt, so it must start from empty state.</param>
    public async Task<T> RunAsync<T>(IReadOnlyList<string> patterns, Func<IAsyncEnumerable<string>, Task<T>> consume, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                // A slot map that is still moving needs time; the next read may then come from a node that has caught up.
                await Task.Delay(retryDelay, time, cancellationToken).ConfigureAwait(false);
            }

            var before = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            if (before.Unsettled.Count == 0)
            {
                var owners = OwnersOf(before);
                var result = await consume(KeysAsync(owners, patterns, cancellationToken)).ConfigureAwait(false);
                var after = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
                if (after.Unsettled.Count == 0 && after.MovedSince(before, Enumerable.Range(0, HashSlot.Count)).Count == 0)
                {
                    return result;
                }
            }
        }

        throw new RedisException($"The slot map kept changing through {attempts} scans, so no scan of every master could be trusted.");
    }

    internal static string EscapeGlob(string text)
    {
        if (text.AsSpan().IndexOfAny("\\*?[]") < 0)
        {
            return text;
        }

        var escaped = new System.Text.StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '?' or '[' or ']')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    // Every slot must have a master, and each master a connected server: a shard that is down is an unavailable owner, never an empty one.
    private IReadOnlyList<IServer> OwnersOf(ClusterLayout layout)
    {
        var covered = layout.Masters.SelectMany(static (MasterLayout m) => m.Slots).Sum(static (SlotRange r) => r.To - r.From + 1);
        if (covered != HashSlot.Count)
        {
            throw new RedisException($"The masters own {covered} of {HashSlot.Count} slots, so a scan of them would miss keys.");
        }

        var connected = servers().Where(s => s.IsConnected && !s.IsReplica).ToArray();
        return [.. layout.Masters.Select(m => connected.FirstOrDefault(s => EndPointMatch.Same(s.EndPoint, m.Endpoint)) ?? throw new RedisException($"The master {m.Endpoint} is not connected, so its keys cannot be scanned."))];
    }

    private async IAsyncEnumerable<string> KeysAsync(IReadOnlyList<IServer> masters, IReadOnlyList<string> patterns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Keys are remembered only when two patterns can match the same key, and only up to a bound: past it the scan fails rather than exhausting memory.
        var seen = GlobPatterns.AnyMayOverlap(patterns) ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (var server in masters)
        {
            foreach (var pattern in patterns)
            {
                await foreach (var key in server.KeysAsync(database, EscapeGlob(connectorPrefix) + pattern, pageSize, 0, 0, Master).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (AcceptKey(key, seen) is { } logical)
                    {
                        yield return logical;
                    }
                }
            }
        }
    }

    private string? AcceptKey(RedisKey key, HashSet<string>? seen)
    {
        var text = (string?)key;
        if (text is null || !text.StartsWith(connectorPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var logical = text[connectorPrefix.Length..];
        if (seen is not null && !seen.Add(logical))
        {
            return null;
        }

        if (seen is { Count: var held } && held > maxDeduplicated)
        {
            alert?.Invoke(new KeyMoveAlert(string.Empty, "scan_dedupe_cap", $"{maxDeduplicated} keys"));
            throw new RedisException($"Overlapping scan patterns matched more than {maxDeduplicated} keys, which is more than the scan can tell apart; the patterns must be made disjoint.");
        }

        return logical;
    }
}
