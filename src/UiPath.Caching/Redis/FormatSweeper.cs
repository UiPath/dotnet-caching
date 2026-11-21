using System.Globalization;
using UiPath.Caching.Serialization;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

/// <summary>Rewrites the strings under a key pattern into the target format, within a rate, a time budget and the migrator's claim window; it never calls an origin and never creates a key.</summary>
/// <remarks>
/// The conversion itself is the <c>rewrite</c> delegate, the one a read's offer runs: the same lossless check, the same compare-and-set that keeps the TTL, the same
/// transaction when the server refuses scripts. A pass looks at strings only; hashes are not swept.
/// </remarks>
internal sealed class FormatSweeper(
    FormatCensus census,
    IRedisConnector redis,
    ValueMigration migration,
    ValueSerializerRegistry registry,
    ICacheValueSerializer target,
    object owner,
    Func<RedisKey, byte[], CancellationToken, ValueTask<RewriteTally>> rewrite,
    Func<byte[], bool> isDefault,
    ICachingTelemetryProvider telemetry,
    TimeProvider clock)
{
    private const char CursorSeparator = '|';
    private const int HeadLength = 2;
    private const string RunIdField = "run_id:";

    public async Task<FormatSweepReport> SweepAsync(CacheKey keyPattern, FormatSweepOptions options, CancellationToken token)
    {
        Validate(options);
        var tally = new Tally();
        string? next = options.Cursor;
        var completed = false;
        string[]? identities = null;
        var primaryIndex = 0;
        ulong cursor = 0;
        try
        {
            var plan = await census.PlanAsync(keyPattern, token).ConfigureAwait(false);
            identities = await IdentitiesAsync(plan.Primaries, plan.Database.Database, token).ConfigureAwait(false);
            (primaryIndex, cursor) = Resume(options.Cursor, identities);
            var limit = Math.Min(options.MaxValueBytes, ValueMigration.MaxValueBytes);
            var pacer = new Pacer(clock, options.RatePerSecond, options.Budget);
            var pages = 0;
            while (true)
            {
                if (primaryIndex >= plan.Primaries.Count)
                {
                    completed = true;
                    break;
                }

                // Between pages here, and between keys in a page, which starts again from its first key when the pass is resumed.
                if (pages > 0 && pacer.BudgetPassed)
                {
                    next = Position(identities[primaryIndex], cursor);
                    break;
                }

                var primary = plan.Primaries[primaryIndex];
                pages++;
                var (after, keys) = await FormatCensus.ScanPageAsync(primary, plan.Database.Database, plan.StringPattern, "string", cursor, options.PageSize, token).ConfigureAwait(false);
                if (!await SweepPageAsync(keys, plan, limit, pacer, tally, token).ConfigureAwait(false))
                {
                    next = Position(identities[primaryIndex], cursor);
                    break;
                }

                if (after == 0)
                {
                    primaryIndex++;
                    cursor = 0;
                }
                else
                {
                    cursor = after;
                }
            }
        }
        catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested)
        {
            // The page that was under way starts again: a key it already converted is current by then.
            next = identities is null ? options.Cursor : Position(identities[primaryIndex], cursor);
        }

        var report = tally.ToReport(next, completed);
        Report(report, options.Group);
        return report;
    }

    private static void Validate(FormatSweepOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RatePerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxValueBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PageSize, 1);
        if (options.Budget is { } budget)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Group);
    }

    private static bool IsTransient(Exception ex) => ex is RedisException or RedisTimeoutException;

    private static bool IsWrongType(RedisServerException ex) => ex.Message.StartsWith("WRONGTYPE", StringComparison.OrdinalIgnoreCase);

    private static string Position(string identity, ulong cursor) =>
        identity + CursorSeparator.ToString() + cursor.ToString(CultureInfo.InvariantCulture);

    // A cursor names a primary and a SCAN cursor on it. Only that same server, by endpoint and run id, goes on from it; a primary that is gone, or an endpoint another
    // server took over, makes the pass start again, since a cursor of one keyspace means nothing in another.
    private static (int Primary, ulong Cursor) Resume(string? text, IReadOnlyList<string> identities)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (0, 0);
        }

        var at = text.LastIndexOf(CursorSeparator);
        if (at <= 0 || !ulong.TryParse(text.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var cursor))
        {
            throw new ArgumentException("The cursor is not one a sweep reported.", nameof(FormatSweepOptions.Cursor));
        }

        var identity = text[..at];
        for (var i = 0; i < identities.Count; i++)
        {
            if (string.Equals(identities[i], identity, StringComparison.Ordinal))
            {
                return (i, cursor);
            }
        }

        return (0, 0);
    }

    // The endpoint, and the server's run id where INFO gives one: a cursor is only continued on the server that made it.
    private static async Task<string[]> IdentitiesAsync(IReadOnlyList<IServer> primaries, int database, CancellationToken token)
    {
        var identities = new string[primaries.Count];
        for (var i = 0; i < identities.Length; i++)
        {
            identities[i] = await IdentityAsync(primaries[i], database, token).ConfigureAwait(false);
        }

        return identities;
    }

    // Asked as a plain command, as SCAN is, because the typed INFO call needs admin mode and a cache's connection has none; a server that does not answer is known by its endpoint alone.
    private static async Task<string> IdentityAsync(IServer primary, int database, CancellationToken token)
    {
        var endpoint = primary.EndPoint?.ToString() ?? string.Empty;
        try
        {
            var reply = await primary.ExecuteAsync(database, "INFO", ["server"], CommandFlags.DemandMaster).WaitAsync(token).ConfigureAwait(false);
            var runId = reply?.ToString()?
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(static line => line.StartsWith(RunIdField, StringComparison.Ordinal))?[RunIdField.Length..];
            return string.IsNullOrEmpty(runId) ? endpoint : endpoint + CursorSeparator + runId;
        }
        catch (Exception ex) when (ex is RedisException or RedisCommandException && !token.IsCancellationRequested)
        {
            return endpoint;
        }
    }

    // The key as the cache holds it: the connector's prefix, which its database adds again, is taken off.
    private static bool TryLogical(RedisKey physical, byte[] prefix, out RedisKey logical)
    {
        var bytes = (byte[]?)physical ?? [];
        if (prefix.Length == 0)
        {
            logical = physical;
            return true;
        }

        if (!bytes.AsSpan().StartsWith(prefix))
        {
            logical = default;
            return false;
        }

        logical = (RedisKey)bytes[prefix.Length..];
        return true;
    }

    // False when the pass is to stop: the target format is not allowed now, so nothing more can be written.
    private async Task<bool> SweepPageAsync(RedisKey[] keys, FormatCensus.ScanPlan plan, int limit, Pacer pacer, Tally tally, CancellationToken token)
    {
        var database = redis.Database;
        var seen = new HashSet<RedisKey>();
        for (var start = 0; start < keys.Length; start += FormatCensus.MaxInFlight)
        {
            var chunk = keys.AsMemory(start, Math.Min(FormatCensus.MaxInFlight, keys.Length - start)).ToArray();
            var heads = await Task.WhenAll(chunk.Select(key => HeadAsync(database, key, plan.PrefixBytes))).WaitAsync(token).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!TryLogical(chunk[i], plan.PrefixBytes, out var key) || !seen.Add(key))
                {
                    continue;
                }

                if (!await SweepKeyAsync(database, key, heads[i], limit, pacer, tally, token).ConfigureAwait(false))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<byte[]?> HeadAsync(IDatabase database, RedisKey physical, byte[] prefix)
    {
        try
        {
            return TryLogical(physical, prefix, out var key)
                ? (byte[]?)await database.StringGetRangeAsync(key, 0, HeadLength - 1, CommandFlags.DemandMaster).ConfigureAwait(false)
                : null;
        }
        catch (RedisServerException ex) when (IsWrongType(ex))
        {
            return null;
        }
    }

    private async Task<bool> SweepKeyAsync(IDatabase database, RedisKey key, byte[]? head, int limit, Pacer pacer, Tally tally, CancellationToken token)
    {
        tally.Scanned++;
        if (head is null)
        {
            tally.Scanned--;
            return true;
        }

        if (head.Length == 0)
        {
            return await CountEmptyAsync(database, key, tally).ConfigureAwait(false);
        }

        if (ValueHeader.TryRead(head, out var formatId, out _))
        {
            if (!registry.TryGetByFormat(formatId, out var source))
            {
                tally.Unreadable++;
                return true;
            }

            if (ReferenceEquals(source, target))
            {
                tally.Current++;
                return true;
            }
        }

        if (!migration.Allows(target))
        {
            tally.Held++;
            return false;
        }

        long length;
        try
        {
            length = await database.StringLengthAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (IsWrongType(ex))
        {
            // The key changed type since the two-byte read: it is another writer's now, as a rewrite that lost a race would find it.
            tally.Changed++;
            return true;
        }

        if (length == 0)
        {
            return await CountEmptyAsync(database, key, tally).ConfigureAwait(false);
        }

        if (length > limit)
        {
            tally.Oversize++;
            return true;
        }

        return await ConvertAsync(database, key, limit, pacer, tally, token).ConfigureAwait(false);
    }

    private async Task<bool> CountEmptyAsync(IDatabase database, RedisKey key, Tally tally)
    {
        // An empty answer is also a key that went away since the scan.
        if (await database.KeyExistsAsync(key, CommandFlags.DemandMaster).ConfigureAwait(false))
        {
            tally.DefaultOrEmpty++;
        }
        else
        {
            tally.Scanned--;
        }

        return true;
    }

    // The value is read with a bound, so what a writer put there since STRLEN cannot be fetched past it, and the bound is what the byte budget is asked for.
    private async Task<bool> ConvertAsync(IDatabase database, RedisKey key, int limit, Pacer pacer, Tally tally, CancellationToken token)
    {
        var migrator = migration.Migrator;
        var bound = limit + 1;

        // Asked before the claim, which a key stopped at would keep for the window; a pass always takes one key in full, so each one moves on.
        if (pacer.Paced && pacer.BudgetPassed)
        {
            return false;
        }

        switch (migrator.TryClaimDirect(owner, key, bound, out var heldLossy))
        {
            case FormatMigrator.ClaimResult.Recent:
                if (heldLossy)
                {
                    tally.Lossy++;
                }
                else
                {
                    tally.Recent++;
                }

                return true;
            case FormatMigrator.ClaimResult.Refused:
                tally.Held++;
                return true;
        }

        var lossy = false;
        try
        {
            // Only a key that is taken in full is paced; one that is current, recent, oversize or left costs no more than its checks.
            await pacer.WaitAsync(token).ConfigureAwait(false);
            var stored = (byte[]?)await database.StringGetRangeAsync(key, 0, limit, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (stored is null)
            {
                tally.Scanned--;
                return true;
            }

            if (stored.Length == 0)
            {
                tally.DefaultOrEmpty++;
                return true;
            }

            if (stored.Length > limit)
            {
                tally.Oversize++;
                return true;
            }

            // The node list is asked once more, now that the value has been read: it may have said no meanwhile.
            if (!migration.Allows(target))
            {
                tally.Held++;
                return false;
            }

            if (!migration.Wants(target, stored))
            {
                // A header means a writer or another sweep converted it since the scan; it is not held back as lossy.
                if (ValueHeader.TryRead(stored, out var formatId, out _))
                {
                    CountHeaded(formatId, tally);
                    return true;
                }

                // Headerless bytes of a proxy that cannot be compared are never looked at.
                lossy = true;
                tally.Lossy++;
                return true;
            }

            var outcome = await rewrite(key, stored, token).ConfigureAwait(false);
            return Count(outcome, stored, tally, ref lossy);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisServerException ex) when (IsWrongType(ex))
        {
            tally.Changed++;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            migrator.NoteFailure(ex);
            tally.Failed++;
            return true;
        }
        finally
        {
            migrator.Finish(owner, key, bound, lossy);
        }
    }

    private void CountHeaded(int formatId, Tally tally)
    {
        if (registry.TryGetByFormat(formatId, out var source) && ReferenceEquals(source, target))
        {
            tally.Current++;
        }
        else
        {
            tally.Unreadable++;
        }
    }

    private bool Count(RewriteTally outcome, byte[] stored, Tally tally, ref bool lossy)
    {
        if (outcome.Done > 0)
        {
            tally.Converted++;
        }
        else if (outcome.Changed > 0)
        {
            tally.Changed++;
        }
        else if (outcome.Held > 0)
        {
            tally.Held++;
            return false;
        }
        else if (isDefault(stored))
        {
            lossy = true;
            tally.DefaultOrEmpty++;
        }
        else
        {
            lossy = true;
            tally.Lossy++;
        }

        return true;
    }

    private void Report(FormatSweepReport report, string group)
    {
        Track("converted", report.Converted, group);
        Track("changed", report.Changed, group);
        Track("lossy", report.Lossy, group);
        Track("default_or_empty", report.DefaultOrEmpty, group);
        Track("oversize", report.Oversize, group);
        Track("held", report.Held, group);
        Track("failed", report.Failed, group);
        Track("current", report.Current, group);
        Track("unreadable", report.Unreadable, group);
        Track("recent", report.Recent, group);
    }

    private void Track(string outcome, long count, string group)
    {
        if (count <= 0)
        {
            return;
        }

        try
        {
            telemetry.TrackMetric(Metrics.FormatSweep, count, [new("outcome", outcome), new("group", group)]);
        }
        catch (Exception ex)
        {
            telemetry.TryTrackException(ex);
        }
    }

    // A key a second, or the rate given: the next is let through once its turn comes, so a burst is never taken.
    private sealed class Pacer(TimeProvider clock, int rate, TimeSpan? budget)
    {
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(1d / rate);
        private readonly long _started = clock.GetTimestamp();
        private long _next = clock.GetTimestamp();
        private long _paced;

        public bool Paced => _paced > 0;

        public bool BudgetPassed => budget is { } limit && clock.GetElapsedTime(_started) >= limit;

        public async ValueTask WaitAsync(CancellationToken token)
        {
            _paced++;
            var now = clock.GetTimestamp();
            var due = _next;
            _next = Math.Max(now, due) + (long)(_interval.TotalSeconds * clock.TimestampFrequency);
            if (due > now)
            {
                await Task.Delay(clock.GetElapsedTime(now, due), clock, token).ConfigureAwait(false);
            }
        }
    }

    private sealed class Tally
    {
        public long Converted { get; set; }

        public long Changed { get; set; }

        public long Lossy { get; set; }

        public long DefaultOrEmpty { get; set; }

        public long Oversize { get; set; }

        public long Held { get; set; }

        public long Failed { get; set; }

        public long Current { get; set; }

        public long Unreadable { get; set; }

        public long Recent { get; set; }

        public long Scanned { get; set; }

        public FormatSweepReport ToReport(string? next, bool completed) => new()
        {
            Converted = Converted,
            Changed = Changed,
            Lossy = Lossy,
            DefaultOrEmpty = DefaultOrEmpty,
            Oversize = Oversize,
            Held = Held,
            Failed = Failed,
            Current = Current,
            Unreadable = Unreadable,
            Recent = Recent,
            Scanned = Scanned,
            NextCursor = completed ? null : next,
            Completed = completed,
        };
    }
}
