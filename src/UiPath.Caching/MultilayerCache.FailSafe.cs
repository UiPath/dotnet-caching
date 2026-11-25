using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching;

internal sealed partial class MultilayerCache
{
    private const string FailSafeBackgroundEvent = "cache.fail_safe.background";
    private const string FailSafeTagCacheName = "cache.name";
    private const string FailSafeTagOutcome = "outcome";
    private const string ReasonError = "error";
    private const string ReasonTimeout = "timeout";

    // Per policy instance, so a value that skips fail-safe is written under an equal policy without it and allocates nothing after the first.
    private static readonly ConditionalWeakTable<CachePolicy, CachePolicy> FailSafeOff = [];

    // The kept value a late store replaces and how long its generator had run (read at that time: the generator's watch has ended, and its fill still compares with a tombstone), in that store's flow; the count of late stores spares other writes the lookup.
    private static readonly AsyncLocal<LateStore?> LateOf = new();

    private CancellationTokenSource? _lateShutdown;
    private int _lateGenerations;
    private int _lateStores;

    /// <summary>Runs just before a late value is stored; tests land a removal there.</summary>
    internal Action? LateStoreStarting { get; set; }

    /// <summary>Runs as a generator becomes a late one, before the cache's disposal can cancel it; tests land a disposal there.</summary>
    internal Action? LateStarting { get; set; }

    /// <summary>Generators still running after fail-safe returned the last value; tests check they end.</summary>
    internal int LateGenerations => Volatile.Read(ref _lateGenerations);

    /// <summary>Whether this write is a late fail-safe store.</summary>
    private bool InLateStore => Volatile.Read(ref _lateStores) != 0 && LateOf.Value is not null;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        try
        {
            // After the cache reads as disposed, so a generator this ends is discarded and not logged as failed.
            Volatile.Read(ref _lateShutdown)?.Cancel();
        }
        catch (AggregateException)
        {
            // A callback a generator registered on its token threw; the cancellation reached every generator all the same.
        }
    }

    /// <summary><paramref name="policy"/> with fail-safe off and every other field as it is.</summary>
    private static CachePolicy WithoutFailSafe(CachePolicy policy) =>
        policy.FailSafeEnabled != true ? policy : FailSafeOff.GetValue(policy, static p => CachePolicyMerger.Merge(new CachePolicy { FailSafeEnabled = false }, p));

    private static void CancelGenerator(object? source) => ((CancellationTokenSource)source!).Cancel();

    /// <summary>A run under a fail-safe policy: as the ordinary run, with the kept value in place of a generator that failed or runs late.</summary>
    /// <param name="store">Stores a generated result, as the ordinary run of its shape does.</param>
    private async ValueTask<object?> RunOrStaleAsync<T, TState, TResult>(
        CacheEntryOptions options,
        TState state,
        Func<TState, CancellationToken, Task<TResult>> generator,
        CachePolicy policy,
        GenerationScope scope,
        bool localOnly,
        Func<MultilayerCache, TResult, DateTimeOffset, ICacheEntry<T?>> entryOf,
        Func<MultilayerCache, CacheEntryOptions, TResult, CachePolicy, GenerationScope, bool, ValueTask<ICacheEntry<T?>>> store)
    {
        ICacheEntry<T?> entry;
        scope = BeginFill(options, scope, localOnly);
        try
        {
            LogCacheMissed(Logged(options, typeof(T)));
            var (generated, stale) = await GenerateOrStaleAsync(options, state, generator, policy, scope, localOnly, entryOf, store).ConfigureAwait(false);
            entry = stale ?? await store(this, options, generated, policy, scope, localOnly).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
        finally
        {
            EndFill(options.CacheKey.Name, scope);
        }

        scope.Complete(entry);
        return entry;
    }

    /// <summary>
    /// The generator's result, or, under fail-safe with a value kept for the key, that value when the generator throws or runs past
    /// <see cref="CachePolicy.FailSafeTimeout"/>. A generator past the timeout runs on and stores its result through <paramref name="store"/>,
    /// unless the key was removed, invalidated or written since it started. Under the distributed lock the soft timeout does not apply: the
    /// lease would end while the generator still runs, and a peer's newer value could be overwritten.
    /// </summary>
    private async ValueTask<(TResult Result, ICacheEntry<T?>? Stale)> GenerateOrStaleAsync<T, TState, TResult>(
        CacheEntryOptions options,
        TState state,
        Func<TState, CancellationToken, Task<TResult>> generator,
        CachePolicy policy,
        GenerationScope scope,
        bool localOnly,
        Func<MultilayerCache, TResult, DateTimeOffset, ICacheEntry<T?>> entryOf,
        Func<MultilayerCache, CacheEntryOptions, TResult, CachePolicy, GenerationScope, bool, ValueTask<ICacheEntry<T?>>> store)
    {
        var name = options.CacheKey.Name;

        // Read before the generator runs: a late result may only replace the value kept when its generator started.
        if (policy.FailSafeTimeout is { } timeout && timeout > TimeSpan.Zero && !DistributedLockOn(policy.Lock) && TryGetStale<T>(name, policy, out var replaced, out _))
        {
            return await GenerateWithinAsync(timeout, replaced, options, state, generator, policy, scope, localOnly, entryOf, store).ConfigureAwait(false);
        }

        try
        {
            return (await FactoryTimeout.RunAsync(state, generator, policy.FactoryTimeout, options.CacheKey, Name, Telemetry, scope.Token, clock: _clock).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && scope.Token.IsCancellationRequested) && TryGetStale<T>(name, policy, out var current, out var kept))
        {
            return (default!, ServeStale(Logged(options, typeof(T)), current, kept, policy, ReasonError, ex));
        }
    }

    /// <summary>
    /// The generation under the soft timeout. The generator gets a token of its own: the caller's, or the run's, cancels it until the timeout,
    /// and only the cache's disposal after, since a late generator outlives the call and the run that started it.
    /// </summary>
    private async ValueTask<(TResult Result, ICacheEntry<T?>? Stale)> GenerateWithinAsync<T, TState, TResult>(
        TimeSpan timeout,
        StaleValue replaced,
        CacheEntryOptions options,
        TState state,
        Func<TState, CancellationToken, Task<TResult>> generator,
        CachePolicy policy,
        GenerationScope scope,
        bool localOnly,
        Func<MultilayerCache, TResult, DateTimeOffset, ICacheEntry<T?>> entryOf,
        Func<MultilayerCache, CacheEntryOptions, TResult, CachePolicy, GenerationScope, bool, ValueTask<ICacheEntry<T?>>> store)
    {
        var name = options.CacheKey.Name;
        var own = new CancellationTokenSource();
        CancellationTokenRegistration? link = scope.Token.UnsafeRegister(CancelGenerator, own);
        var late = false;
        try
        {
            var generation = FactoryTimeout.RunAsync(state, generator, policy.FactoryTimeout, options.CacheKey, Name, Telemetry, own.Token, clock: _clock);
            if (!generation.IsCompleted)
            {
                using var timer = new CancellationTokenSource();
                // Task.Delay throws beyond about 49.7 days, and here the generator has already started.
                var elapsed = Task.Delay(BoundedTimeout(timeout), _clock, timer.Token);
                if (await Task.WhenAny(generation, elapsed).ConfigureAwait(false) != generation && !scope.Token.IsCancellationRequested && TryGetStale<T>(name, policy, out var current, out var kept))
                {
                    await link.Value.DisposeAsync().ConfigureAwait(false);
                    link = null;

                    // A cancellation that landed before the link went has ended the generator: the call is answered as cancelled, below.
                    if (!own.IsCancellationRequested)
                    {
                        late = true;

                        // For a caller that joined the run without fail-safe; observed here, since such a caller may stop waiting for it.
                        var answer = LateEntryAsync(generation, entryOf, options.Expiration);
                        answer.Forget();
                        StartLateFinish(generation, own, options, typeof(T), policy, localOnly, replaced, _background.FillElapsed(name, scope.Fill), store);
                        return (default!, ServeStale(Logged(options, typeof(T)), current, kept, policy, ReasonTimeout, null, answer));
                    }
                }

                await timer.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                return (await generation.ConfigureAwait(false), null);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && scope.Token.IsCancellationRequested) && TryGetStale<T>(name, policy, out var current, out var kept))
            {
                return (default!, ServeStale(Logged(options, typeof(T)), current, kept, policy, ReasonError, ex));
            }
        }
        finally
        {
            if (!late)
            {
                if (link is { } registration)
                {
                    await registration.DisposeAsync().ConfigureAwait(false);
                }

                own.Dispose();
            }
        }
    }

    /// <summary>The late generator's own answer: the entry a run would have made of its result, an expiration the result names included.</summary>
    private async Task<ICacheEntry<T?>> LateEntryAsync<T, TResult>(Task<TResult> generation, Func<MultilayerCache, TResult, DateTimeOffset, ICacheEntry<T?>> entryOf, DateTimeOffset expiration) =>
        entryOf(this, await generation.ConfigureAwait(false), expiration);

    /// <summary>
    /// Whether <paramref name="kept"/>, which a run fell back to, answers a caller that joined the run: false when the caller gets what the run
    /// would have answered without fail-safe, and null when the value is no longer the one kept, so the caller needs a generation of its own.
    /// </summary>
    private bool? ServesJoined<T>(string name, CachePolicy policy, StaleValue kept)
    {
        if (policy.FailSafeEnabled != true)
        {
            return false;
        }

        if (TryGetStale<T>(name, policy, out var current, out _))
        {
            return ReferenceEquals(current, kept) ? true : null;
        }

        // Still kept and not served: it is past this caller's own stale age, or the local tier may not be read.
        return StaleOrNull?.IsCurrent(name, kept) == true ? false : null;
    }

    private void StartLateFinish<T, TResult>(Task<TResult> generation, CancellationTokenSource own, CacheEntryOptions options, Type entryType, CachePolicy policy, bool localOnly, StaleValue replaced, TimeSpan? ran, Func<MultilayerCache, CacheEntryOptions, TResult, CachePolicy, GenerationScope, bool, ValueTask<ICacheEntry<T?>>> store)
    {
        Interlocked.Increment(ref _lateGenerations);
        FinishLateAsync(generation, own, options.WithToken(CancellationToken.None), entryType, policy, localOnly, replaced, ran is { } elapsed ? (elapsed, _clock.GetTimestamp()) : null, store).Forget();
    }

    /// <summary>From here only the cache's disposal cancels the generator, at once when it has begun.</summary>
    private CancellationTokenRegistration CancelOnShutdown(CancellationTokenSource own)
    {
        LateStarting?.Invoke();
        var shutdown = default(CancellationTokenRegistration);
        try
        {
            // A source the disposal has cancelled runs the callback here.
            shutdown = LateShutdown().UnsafeRegister(CancelGenerator, own);
            if (Disposed)
            {
                // A disposal that ran before the source existed cancelled nothing.
                CancelGenerator(own);
            }
        }
        catch (AggregateException)
        {
            // As in Dispose: a callback the generator registered on its token threw, and the cancellation reached it all the same.
        }

        return shutdown;
    }

    /// <summary>Cancelled when the cache is disposed, which ends the generators fail-safe stopped waiting for.</summary>
    private CancellationToken LateShutdown()
    {
        if (Volatile.Read(ref _lateShutdown) is { } source)
        {
            return source.Token;
        }

        var created = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _lateShutdown, created, null) is not { } won)
        {
            return created.Token;
        }

        created.Dispose();
        return won.Token;
    }

    private async Task FinishLateAsync<T, TResult>(Task<TResult> generation, CancellationTokenSource own, CacheEntryOptions options, Type entryType, CachePolicy policy, bool localOnly, StaleValue replaced, (TimeSpan Elapsed, long At)? ran, Func<MultilayerCache, CacheEntryOptions, TResult, CachePolicy, GenerationScope, bool, ValueTask<ICacheEntry<T?>>> store)
    {
        var outcome = "failed";
        var shutdown = default(CancellationTokenRegistration);
        try
        {
            // Inside the try, so the generation is awaited and cleaned up whatever a cancellation that runs here throws.
            shutdown = CancelOnShutdown(own);
            var result = await generation.ConfigureAwait(false);
            if (!Disposed && StillKept(options.CacheKey.Name, replaced))
            {
                LateStoreStarting?.Invoke();
                // The write checks again in the key's turn; this check only spares a store that cannot be kept.
                Interlocked.Increment(ref _lateStores);
                LateOf.Value = new LateStore(replaced, ran);
                try
                {
                    await store(this, options, result, policy, new GenerationScope(null, options.Token), localOnly).ConfigureAwait(false);
                }
                finally
                {
                    LateOf.Value = null;
                    Interlocked.Decrement(ref _lateStores);
                }

                outcome = "stored";
            }
            else
            {
                outcome = "discarded";
            }
        }
        catch (Exception ex) when (!Disposed)
        {
            LogLateGenerationFailed(ex, Logged(options, entryType));
        }
        catch (Exception)
        {
            // Ended after the cache was disposed: nothing was stored and nothing is wrong.
            outcome = "discarded";
        }
        finally
        {
            await shutdown.DisposeAsync().ConfigureAwait(false);
            own.Dispose();
            Telemetry.TryTrackEvent(FailSafeBackgroundEvent, [new(FailSafeTagCacheName, Name), new(FailSafeTagOutcome, outcome)]);
            Interlocked.Decrement(ref _lateGenerations);
        }
    }

    /// <summary>How long the generator of a late store had run by now, which a fill compares with a tombstone; null for any other write.</summary>
    private TimeSpan? LateGeneratorElapsed() =>
        Volatile.Read(ref _lateStores) != 0 && LateOf.Value?.Ran is { } ran ? ran.Elapsed + _clock.GetElapsedTime(ran.At) : null;

    /// <summary>Whether a write may go on: true unless it is a late store whose replaced value is no longer the one kept. Checked in the key's write turn.</summary>
    private bool LateStillCurrent(in CacheEntryOptions options) =>
        Volatile.Read(ref _lateStores) == 0 || LateOf.Value is not { } late || (!Disposed && StillKept(options.CacheKey.Name, late.Replaced));

    /// <summary>Drops the key's kept value for a generator answer the cache does not store, unless a write since the generator started kept it: a late answer drops only the value its generator replaced, any other only a value with no live entry.</summary>
    private void DropKeptForUnstored(string name)
    {
        if (Volatile.Read(ref _lateStores) != 0 && LateOf.Value is { } late)
        {
            StaleOrNull?.Drop(name, late.Replaced);
        }
        else
        {
            DropKeptUnlessLive(name);
        }
    }

    /// <summary>Stores a late value as a run stores a generated one, then announces it: other nodes may be answering misses with the value it replaces, in a cooldown that reads nothing.</summary>
    private async ValueTask<bool> StoreLateAndAnnounceAsync<T>(CacheEntryOptions options, T? value, bool disconnected, CachePolicy policy, ICacheEntry<T?> entry)
    {
        var layered = Layered<T>();
        // Written before the event, so a node it wakes reads the new value; a write the key's turn refused, or one that failed, is not announced.
        var (written, kept, counted) = await layered.SetInnerAsync(options, value, disconnected, policy, keepRefused: true, entry).ConfigureAwait(false);
        if (written)
        {
            await layered.PublishSetAsync(options, counted).ConfigureAwait(false);
        }

        return kept;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The generator of {CacheKey} that fail-safe stopped waiting for failed.")]
    private partial void LogLateGenerationFailed(Exception ex, LoggedKey cacheKey);

    private readonly record struct LateStore(StaleValue Replaced, (TimeSpan Elapsed, long At)? Ran);
}
