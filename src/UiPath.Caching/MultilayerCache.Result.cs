namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        return GetOrAddResultInternalAsync(cacheKey, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        return GetOrAddResultInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        return GetOrAddResultInternalAsync(cacheKey, generator, writeExpiration, duration, rehydrateJitter: null, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        return GetOrAddResultInternalAsync(cacheKey, state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        return GetOrAddResultInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        return GetOrAddResultInternalAsync(cacheKey, state, generator, writeExpiration, duration, rehydrateJitter: null, policy, _localMemorySetter.Writes, localMissed: false, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocalEntry<T>(cacheKey, policy, token, out var hit);
        return probe == LocalProbe.Hit
            ? new ValueTask<CacheResult<T>>(ResultOf(hit!, generated: false))
            : GetOrAddResultInternalAsync(new CacheKey(cacheKey), generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocalEntry<T>(cacheKey, policy, token, out var hit);
        return probe == LocalProbe.Hit
            ? new ValueTask<CacheResult<T>>(ResultOf(hit!, generated: false))
            : GetOrAddResultInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T>(Span<char> cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocalEntry<T>(cacheKey, policy, token, out var hit);
        return probe == LocalProbe.Hit
            ? new ValueTask<CacheResult<T>>(ResultOf(hit!, generated: false))
            : GetOrAddResultInternalAsync(new CacheKey(cacheKey), generator, writeExpiration, duration, rehydrateJitter: null, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocalEntry<T>(cacheKey, policy, token, out var hit);
        return probe == LocalProbe.Hit
            ? new ValueTask<CacheResult<T>>(ResultOf(hit!, generated: false))
            : GetOrAddResultInternalAsync(new CacheKey(cacheKey), state, generator, expiration: null, ResolveDuration(policy), policy.JitterMaxDuration, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocalEntry<T>(cacheKey, policy, token, out var hit);
        return probe == LocalProbe.Hit
            ? new ValueTask<CacheResult<T>>(ResultOf(hit!, generated: false))
            : GetOrAddResultInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy, seen, probe == LocalProbe.Miss, token);
    }

    public ValueTask<CacheResult<T>> GetOrAddResultAsync<T, TState>(Span<char> cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var (writeExpiration, duration) = CallerWrite(expiration);
        policy ??= _defaultPolicy;
        var seen = _localMemorySetter.Writes;
        var probe = ProbeGetOrAddLocalEntry<T>(cacheKey, policy, token, out var hit);
        return probe == LocalProbe.Hit
            ? new ValueTask<CacheResult<T>>(ResultOf(hit!, generated: false))
            : GetOrAddResultInternalAsync(new CacheKey(cacheKey), state, generator, writeExpiration, duration, rehydrateJitter: null, policy, seen, probe == LocalProbe.Miss, token);
    }

    /// <summary>A null that is not cached has no expiration to report, and neither has an entry without one.</summary>
    private CacheResult<T> ResultOf<T>(ICacheEntry<T?> entry, bool generated) =>
        new(entry.Value, entry.Expiration == DateTimeOffset.MaxValue || (entry.Value is null && !_multiLayerCacheOptions.CacheNullValues) ? null : entry.Expiration, generated);

    /// <summary>As <see cref="GetOrAddInternalAsync{T}"/>, reporting whether this call ran its generator; a hit allocates nothing.</summary>
    private ValueTask<CacheResult<T>> GetOrAddResultInternalAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T?>(cacheKey, token, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                Layered<T>().TryRehydrate(cacheKey, local.Expiration, local.Value, generator, policy, effectiveDuration, rehydrateJitter);
                return new ValueTask<CacheResult<T>>(ResultOf(local, generated: false));
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddResultCoreAsync(cacheKey, generator, expiration, effectiveDuration, rehydrateJitter, policy, seen, localMissed, token);
    }

    private ValueTask<CacheResult<T>> GetOrAddResultInternalAsync<T, TState>(CacheKey cacheKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        NotCacheableException.ThrowIfNotCacheable<T>();
        if (!localMissed && !token.IsCancellationRequested)
        {
            var probe = ProbeLocal<T?>(cacheKey, token, out var local);
            if (probe == LocalProbe.Hit && local!.Found)
            {
                var layered = Layered<T>();
                if (layered.ShouldRehydrate(local.Value, policy, effectiveDuration))
                {
                    layered.TriggerRehydrate(cacheKey, local.Expiration, Bind(state, generator), policy, effectiveDuration, rehydrateJitter);
                }

                return new ValueTask<CacheResult<T>>(ResultOf(local, generated: false));
            }

            localMissed = probe == LocalProbe.Miss;
        }

        return GetOrAddResultCoreAsync(cacheKey, Bind(state, generator), expiration, effectiveDuration, rehydrateJitter, policy, seen, localMissed, token);
    }

    /// <summary>The miss path: the generator is wrapped to record that this call ran it, so a call that joined another's run, or found a value after the distributed lock, reports false.</summary>
    private async ValueTask<CacheResult<T>> GetOrAddResultCoreAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset? expiration, TimeSpan effectiveDuration, TimeSpan? rehydrateJitter, CachePolicy policy, long seen, bool localMissed, CancellationToken token)
    {
        var options = _entryBuilder.BuildEntryOptions<T>(cacheKey, expiration ?? _clock.ToDateTimeOffset(ApplyJitter(effectiveDuration, rehydrateJitter)), token);
        var entry = localMissed ? await Layered<T>().FetchInnerAsync(options, policy).ConfigureAwait(false) : await GetCacheEntryInnerAsync<T>(options, policy).ConfigureAwait(false);
        if (entry.Found)
        {
            Layered<T>().TryRehydrate(cacheKey, entry.Expiration, entry.Value, generator, policy, effectiveDuration, rehydrateJitter);
            return ResultOf(entry, generated: false);
        }

        var recorder = GeneratorRecorder<T>.For(generator);
        var generated = await GetOrGenerateEntryAsync<T, Func<CancellationToken, Task<T?>>>(options, recorder.Generator, policy, static (cache, o, g, p, scope) => cache.RunGeneratorAndStoreEntryAsync(o, g, p, scope), seen, token).ConfigureAwait(false);
        return ResultOf(generated, recorder.Ran);
    }
}
