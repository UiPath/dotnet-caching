using System.Runtime.CompilerServices;
using UiPath.Caching.Serialization;

namespace UiPath.Caching.Redis;

/// <summary>The values a generator made, stored as fills that know how long the generator ran, so that a removal that landed while it ran is not undone; only with a remove tombstone.</summary>
internal sealed partial class RedisCache
{
    private static void SetGeneratedFillExpiration<T>(IInFlightEnd<T?> run, DateTimeOffset deadline) =>
        run.Expiration = deadline == DateTimeOffset.MaxValue ? null : deadline;

    private async Task<T?> GenerateAndFillAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<T?>> generator, TimeSpan? factoryTimeout, TimeSpan lifetime, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        var started = Clock.GetTimestamp();
        var value = await RunGeneratorAsync(cacheKey, state, generator, factoryTimeout, run.Token).ConfigureAwait(false);
        return !IsDefault(value) || _cacheNullValues ? await FillGeneratedAsync(cacheKey, redisKey, value, lifetime, started, format, run).ConfigureAwait(false) : value;
    }

    private async Task<GeneratedValue<T>> GenerateAndFillExpiringAsync<T, TState, TFormat>(CacheKey cacheKey, RedisKey redisKey, TState state, Func<TState, CancellationToken, Task<GeneratedValue<T>>> generator, CachePolicy? policy, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        var started = Clock.GetTimestamp();
        var generated = await RunGeneratorAsync(cacheKey, state, generator, (policy ?? DefaultPolicy)?.FactoryTimeout, run.Token).ConfigureAwait(false);
        var duration = generated.Expiration is { } expiration ? Remaining(expiration) : PolicyDuration(policy);
        if (duration is not { } lifetime || (IsDefault(generated.Value) && !_cacheNullValues))
        {
            return generated;
        }

        return generated with { Value = await FillGeneratedAsync(cacheKey, redisKey, generated.Value, lifetime, started, format, run).ConfigureAwait(false) };
    }

    /// <summary>The value to answer: the key's own when one was stored meanwhile, the generator's otherwise.</summary>
    private async Task<T?> FillGeneratedAsync<T, TFormat>(CacheKey cacheKey, RedisKey redisKey, T? value, TimeSpan lifetime, long startedAt, TFormat format, IInFlightEnd<T?> run)
        where TFormat : struct, IValueFormat
    {
        var deadline = Clock.ToDateTimeOffset(lifetime);
        for (var attempt = 0; attempt < FillAttempts; attempt++)
        {
            var filled = await TryFillInternalAsync(cacheKey, redisKey, value, deadline, format, NewFence(Clock.GetElapsedTime(startedAt)), run.Token).ConfigureAwait(false);
            switch (filled.Outcome)
            {
                case FillOutcome.Added:
                    SetGeneratedFillExpiration(run, deadline);
                    return value;
                case FillOutcome.Existed:
                    run.Expiration = filled.Expiration == DateTimeOffset.MaxValue ? null : filled.Expiration;
                    run.ExpirationUnread = filled.Expiration is null;
                    return filled.Existing;
                case FillOutcome.Unreadable:
                    switch (await ReplaceUnreadableAsync(cacheKey, redisKey, value, deadline, format, run.Token).ConfigureAwait(false))
                    {
                        case UnreadableReplace.Stored:
                            SetGeneratedFillExpiration(run, deadline);
                            return value;
                        case UnreadableReplace.Changed:
                            continue;
                        default:
                            return value;
                    }

                default:
                    return value;
            }
        }

        return value;
    }

    private ValueTask<KeyValuePair<TState, T?>[]> GetOrAddFencedBatchAsync<T, TState, TArg>(
        KeyValuePair<CacheKey, TState>[] entries,
        TArg argument,
        Func<TArg, TState[], CancellationToken, Task<KeyValuePair<TState, T?>[]>> generator,
        StoreLifetime lifetime,
        CachePolicy? policy,
        CancellationToken token)
        where TState : notnull
    {
        var started = new StrongBox<long>();
        return BatchGetOrAdd.RunAsync(
            this,
            entries,
            argument,
            (arg, states, ct) =>
            {
                started.Value = Clock.GetTimestamp();
                return generator(arg, states, ct);
            },
            (pairs, ct) => FillGeneratedBatchAsync(pairs, lifetime, policy, started.Value, ct),
            policy,
            token);
    }

    private ValueTask<bool> FillGeneratedBatchAsync<T>(KeyValuePair<CacheKey, T?>[] pairs, StoreLifetime lifetime, CachePolicy? policy, long startedAt, CancellationToken token)
    {
        var deadline = Clock.ToDateTimeOffset(lifetime.Resolve(this, policy));
        var format = FormatFor(policy, forWrite: true, alsoReads: true);
        return format.IsDefault
            ? FillGeneratedBatchAsync(pairs, deadline, startedAt, default(PlainFormat), token)
            : FillGeneratedBatchAsync(pairs, deadline, startedAt, format, token);
    }

    private async ValueTask<bool> FillGeneratedBatchAsync<T, TFormat>(KeyValuePair<CacheKey, T?>[] pairs, DateTimeOffset deadline, long startedAt, TFormat format, CancellationToken token)
        where TFormat : struct, IValueFormat
    {
        var stored = true;
        var pending = pairs;
        var attempt = 0;
        while (pending.Length > 0)
        {
            if (attempt == FillAttempts)
            {
                return false;
            }

            var filled = await TryFillManyInternalAsync(pending, deadline, format, NewFence(Clock.GetElapsedTime(startedAt)), token).ConfigureAwait(false);
            List<KeyValuePair<CacheKey, T?>>? changed = null;
            for (var i = 0; i < pending.Length; i++)
            {
                if (filled[i].Outcome == FillOutcome.Unreadable)
                {
                    switch (await ReplaceUnreadableAsync(pending[i].Key, ToRedisKey(pending[i].Key, token), pending[i].Value, deadline, format, token).ConfigureAwait(false))
                    {
                        case UnreadableReplace.Changed:
                            (changed ??= []).Add(pending[i]);
                            break;
                        case UnreadableReplace.Refused:
                            stored = false;
                            break;
                    }
                }

                stored &= filled[i].Outcome != FillOutcome.Failed;
            }

            pending = changed is null ? [] : [.. changed];
            attempt++;
        }

        return stored;
    }

    /// <summary>How long a batch's values live: the policy's, a duration or a deadline.</summary>
    private readonly record struct StoreLifetime(TimeSpan? Span, DateTimeOffset? At)
    {
        public StoreLifetime(TimeSpan span)
            : this(span, null)
        {
        }

        public StoreLifetime(DateTimeOffset at)
            : this(null, at)
        {
        }

        public TimeSpan Resolve(RedisCache cache, CachePolicy? policy) =>
            this switch
            {
                { Span: { } expiration } => CallerDuration(expiration),
                { At: { } expiration } => cache.CallerDuration(expiration),
                _ => cache.PolicyDuration(policy),
            };
    }
}
