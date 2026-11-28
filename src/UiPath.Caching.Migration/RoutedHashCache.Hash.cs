namespace UiPath.Caching.Migration;

// The IHashCache<T> surface of a routed hash cache: every abstract member is implemented here, explicitly, and has one rule in the test table HashMemberRules (Migration tests, Support/SurfaceRules).
// The span-key members and the sealed members of the interface forward to these, so they need no rule of their own.
internal sealed partial class RoutedHashCache<T>
{
    string IHashCache<T>.Name => _registration.Name;

    ValueTask<T?> IHashCache<T>.GetItemAsync(CacheKey cacheKey, string field, CancellationToken token) => ItemAsync(KeyOf(cacheKey), field, token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetAsync(CacheKey cacheKey, CancellationToken token) => FieldsAsync(KeyOf(cacheKey), null, token);

    ValueTask<IDictionary<string, T?>> IHashCache<T>.GetAsync(CacheKey cacheKey, string[] fields, CancellationToken token) => FieldsAsync(KeyOf(cacheKey), fields, token);

    ValueTask<ICacheEntry<IDictionary<string, T?>>> IHashCache<T>.GetCacheEntryAsync(CacheKey cacheKey, CancellationToken token) => EntryAsync(KeyOf(cacheKey), token);

    ValueTask<bool> IHashCache<T>.ContainsAsync(CacheKey cacheKey, CancellationToken token) => ContainsAsync(KeyOf(cacheKey), token);

    ValueTask<TimeSpan?> IHashCache<T>.TimeToLiveAsync(CacheKey cacheKey, CancellationToken token) => TimeToLiveAsync(KeyOf(cacheKey), token);

    ValueTask<DateTimeOffset?> IHashCache<T>.ExpireTimeAsync(CacheKey cacheKey, CancellationToken token) => ExpireTimeAsync(KeyOf(cacheKey), token);

    ValueTask<IDictionary<string, string?>?> IHashCache<T>.GetMetadataAsync(CacheKey cacheKey, CancellationToken token) => MetadataAsync(KeyOf(cacheKey), token);

    ValueTask<bool> IHashCache<T>.SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, CancellationToken token) => StoredAsync(KeyOf(cacheKey), values, HashCacheSetOption.KeyReplace, default, null, token);

    ValueTask<bool> IHashCache<T>.SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, TimeSpan expiration, CancellationToken token) => StoredAsync(KeyOf(cacheKey), values, HashCacheSetOption.KeyReplace, Rel(expiration), null, token);

    ValueTask<bool> IHashCache<T>.SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, DateTimeOffset expiration, CancellationToken token) => StoredAsync(KeyOf(cacheKey), values, HashCacheSetOption.KeyReplace, Abs(expiration), null, token);

    ValueTask<bool> IHashCache<T>.SetAsync(CacheKey cacheKey, IDictionary<string, T?> values, HashCacheEntryOptions options, CancellationToken token) => StoredAsync(KeyOf(cacheKey), values, options.SetOption, ExpiryOf(options), options.Metadata, token);

    ValueTask<bool> IHashCache<T>.RefreshAsync(CacheKey cacheKey, CancellationToken token) => RefreshedAsync(KeyOf(cacheKey), default, false, null, token);

    ValueTask<bool> IHashCache<T>.RefreshAsync(CacheKey cacheKey, TimeSpan expiration, CancellationToken token) => RefreshedAsync(KeyOf(cacheKey), Rel(expiration), false, null, token);

    ValueTask<bool> IHashCache<T>.RefreshAsync(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token) => RefreshedAsync(KeyOf(cacheKey), Abs(expiration), false, null, token);

    ValueTask<bool> IHashCache<T>.RefreshAsync(CacheKey cacheKey, HashCacheEntryOptions options, CancellationToken token) => RefreshedAsync(KeyOf(cacheKey), ExpiryOf(options), true, options.Metadata, token);

    ValueTask<bool> IHashCache<T>.RemoveAsync(CacheKey cacheKey, CancellationToken token) => RemovedAsync(KeyOf(cacheKey), token);

    ValueTask<bool> ISpanKeyHashCache<T>.RemoveFieldsAsync(CacheKey cacheKey, string[] fields, CancellationToken token) => FieldsRemovedAsync(KeyOf(cacheKey), fields, token);

    ValueTask<bool> IHashCache<T>.SetMetadataAsync(CacheKey cacheKey, IDictionary<string, string?> metadata, CancellationToken token) =>
        throw new NotSupportedException($"Hash cache '{_registration.Name}' cannot set metadata on its own: write it with the fields through SetAsync with options, or with a refresh.");

    private static string KeyOf(CacheKey cacheKey) => cacheKey.IsNull ? throw new ArgumentException("A cache key is required.", nameof(cacheKey)) : cacheKey.Name;

    private static IDictionary<string, T?> WholeHash(HashRead<T> read) => read.Kind == ReadOutcome.Found && read.Fields is { } found ? found : Empty();

    // Every field asked for is answered, a missing one as null, whether the hash is there or not; a read that failed answers none.
    private static Dictionary<string, T?> Selected(HashRead<T> read, string[] fields)
    {
        if (read.Kind is not (ReadOutcome.Found or ReadOutcome.Absent))
        {
            return Empty();
        }

        var selected = new Dictionary<string, T?>(fields.Length, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            selected[field] = read.Fields is { } held && held.TryGetValue(field, out var value) ? value : null;
        }

        return selected;
    }

    private static Dictionary<string, T?> Empty() => new(StringComparer.Ordinal);

    private static CacheExpiry Rel(TimeSpan expiration) => CacheExpiry.After(CacheExpiration.ThrowIfNotPositive(expiration));

    private CacheExpiry Abs(DateTimeOffset expiration) => CacheExpiry.At(CacheExpiration.ThrowIfNotFuture(expiration, _core.Clock.GetUtcNow()));

    private CacheExpiry ExpiryOf(HashCacheEntryOptions options)
    {
        if (options.ExpireTime is { } at)
        {
            return Abs(at);
        }

        return options.TimeToLive is { } life ? Rel(life) : default;
    }

    // No expiry given: the registration's default, as a single write applies it.
    private CacheExpiry Resolve(CacheExpiry expiry)
    {
        if (expiry.Relative is not null || expiry.Deadline is not null)
        {
            return expiry;
        }

        return _registration.DefaultExpiry is { } life ? CacheExpiry.After(life) : CacheExpiry.At(DateTimeOffset.MaxValue);
    }

    private async ValueTask<HashRead<T>> ReadSurfaceAsync(string key, string[]? fields, CancellationToken token)
    {
        using var admission = Admit(out var route);
        try
        {
            return (await ReadAsync(key, fields, route, token).ConfigureAwait(false)).Read;
        }
        catch (CacheUnavailableException)
        {
            return HashRead<T>.Of(ReadOutcome.Unavailable);
        }
    }

    private async ValueTask<T?> ItemAsync(string key, string field, CancellationToken token)
    {
        HashFields.ValidateFieldForRead(field);
        var read = await ReadSurfaceAsync(key, [field], token).ConfigureAwait(false);
        return read.Fields is { } fields && fields.TryGetValue(field, out var value) ? value : null;
    }

    private async ValueTask<IDictionary<string, T?>> FieldsAsync(string key, string[]? fields, CancellationToken token)
    {
        // An empty selector reads the whole hash, as the native cache does.
        if (fields is { Length: 0 })
        {
            fields = null;
        }

        if (fields is not null)
        {
            HashFields.ValidateFieldsForRead(fields);
        }

        var read = await ReadSurfaceAsync(key, fields, token).ConfigureAwait(false);
        return fields is null ? WholeHash(read) : Selected(read, fields);
    }

    private async ValueTask<ICacheEntry<IDictionary<string, T?>>> EntryAsync(string key, CancellationToken token)
    {
        var read = await ReadSurfaceAsync(key, null, token).ConfigureAwait(false);
        if (read.Kind == ReadOutcome.Found && read.Fields is { } found)
        {
            return Entries.Create(found, read.Expiration ?? DateTimeOffset.MaxValue, read.Metadata);
        }

        // A hash cached empty is there, with nothing in it.
        return read.Stops
            ? Entries.Create<IDictionary<string, T?>>(Empty(), read.Expiration ?? DateTimeOffset.MaxValue, read.Metadata)
            : Entries.Create<IDictionary<string, T?>>(Empty(), DateTimeOffset.MinValue);
    }

    private async ValueTask<IDictionary<string, string?>?> MetadataAsync(string key, CancellationToken token)
    {
        var read = await ReadSurfaceAsync(key, null, token).ConfigureAwait(false);
        return read.Kind == ReadOutcome.Found ? read.Metadata : null;
    }

    // The client says whether the key is there when it can, as the library does for a hash cached empty; otherwise a read answers.
    private async ValueTask<bool> ContainsAsync(string key, CancellationToken token)
    {
        using var admission = Admit(out var route);
        try
        {
            if (!route.Hold && await Client(route).ContainsAsync(key, token).ConfigureAwait(false) is { } present)
            {
                return present;
            }

            var read = (await ReadAsync(key, null, route, token).ConfigureAwait(false)).Read;
            return read.Kind == ReadOutcome.Found || read.Stops;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken token)
    {
        using var admission = Admit(out var route);
        try
        {
            return await Client(route).ExpireTimeAsync(key, token).ConfigureAwait(false);
        }
        catch (CacheUnavailableException)
        {
            return null;
        }
    }

    private async ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken token)
    {
        if (await ExpireTimeAsync(key, token).ConfigureAwait(false) is not { } expires)
        {
            return null;
        }

        var left = expires - _core.Clock.GetUtcNow();
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    private async ValueTask<bool> StoredAsync(string key, IDictionary<string, T?> values, HashCacheSetOption mode, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken token)
    {
        HashFields.ValidateForWrite(values);
        using var admission = Admit(out var route);
        token.ThrowIfCancellationRequested();
        if (mode == HashCacheSetOption.HashReplace && values.Count == 0)
        {
            return true;
        }

        var options = new HashWriteOptions(mode, Deadline(expiry), metadata);
        // The source cannot keep metadata, so a write that carries it never goes there from the library.
        var path = metadata is null ? route : route with { NoFallback = true };
        try
        {
            var outcome = await MutateAsync(key, path, (client, ct) => StoreOnClientAsync(client, key, values, options, ct), null, token).ConfigureAwait(false);
            return outcome == WriteOutcome.Committed;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private ValueTask<WriteResult> StoreOnClientAsync(IHashPathClient<T> client, string key, IDictionary<string, T?> values, HashWriteOptions options, CancellationToken token)
    {
        if (options.Deadline is not { } at || at > _core.Clock.GetUtcNow())
        {
            return client.SetAsync(key, values, options, token);
        }

        return options.Mode == HashCacheSetOption.KeyReplace ? client.RemoveAsync(key, token) : ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);
    }

    private async ValueTask<bool> RefreshedAsync(string key, CacheExpiry expiry, bool withMetadata, IDictionary<string, string?>? metadata, CancellationToken token)
    {
        using var admission = Admit(out var route);
        var resolved = Resolve(expiry);
        Command command = withMetadata ? (client, ct) => client.RefreshAsync(key, resolved, metadata, ct) : (client, ct) => client.RefreshAsync(key, resolved, ct);
        try
        {
            return await MutateAsync(key, route with { NoFallback = true }, command, null, token).ConfigureAwait(false) == WriteOutcome.Committed;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<bool> RemovedAsync(string key, CancellationToken token)
    {
        using var admission = Admit(out var route);
        try
        {
            return await MutateAsync(key, route, (client, ct) => client.RemoveAsync(key, ct), null, token).ConfigureAwait(false) == WriteOutcome.Committed;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }

    private async ValueTask<bool> FieldsRemovedAsync(string key, string[] fields, CancellationToken token)
    {
        HashFields.ValidateFieldsForWrite(fields);
        using var admission = Admit(out var route);
        try
        {
            return await MutateAsync(key, route, (client, ct) => client.RemoveFieldsAsync(key, fields, ct), null, token).ConfigureAwait(false) == WriteOutcome.Committed;
        }
        catch (CacheUnavailableException)
        {
            return false;
        }
    }
}
