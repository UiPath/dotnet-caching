namespace UiPath.Caching;

public abstract partial class MultilayerCacheBase
{
    /// <summary>The flow of one entry through the local and the inner tier, shared by the caches; a struct over the shape so none of it is a virtual call.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2436:Types and methods should not have too many generic parameters", Justification = "A struct shape keeps the shared flow free of virtual calls; C# has no associated types, so the value, options and write result are type parameters.")]
    private protected readonly struct LayeredEntry<TShape, TValue, TOptions, TWrite>(MultilayerCacheBase cache, TShape shape)
        where TShape : struct, ILayeredShape<TValue, TOptions, TWrite>
        where TOptions : struct, ILayeredOptions<TOptions>
    {
        public void TryRehydrate(CacheKey originalCacheKey, DateTimeOffset entryExpiration, TValue? currentValue, Func<CancellationToken, Task<TValue>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter)
        {
            if (ShouldRehydrate(currentValue, policy, duration))
            {
                TriggerRehydrate(originalCacheKey, entryExpiration, generator, policy, duration, rehydrateJitter);
            }
        }

        public bool ShouldRehydrate(TValue? currentValue, CachePolicy policy, TimeSpan duration)
        {
            if (policy.RehydrateEnabled != true || policy.Rehydrate is null)
            {
                return false;
            }
            if (shape.IsEmpty(currentValue) && cache._multiLayerCacheOptions.CacheNullValues)
            {
                return false;
            }
            return duration > TimeSpan.Zero && duration != TimeSpan.MaxValue;
        }

        /// <summary>Hands the rehydration to the coordinator; apart from <see cref="TryRehydrate"/> so its early returns do not allocate the closure.</summary>
        public void TriggerRehydrate(CacheKey originalCacheKey, DateTimeOffset entryExpiration, Func<CancellationToken, Task<TValue>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter)
        {
            // A struct's lambda cannot capture its own members.
            var flow = this;
            cache._rehydrator.TryTrigger(
                originalCacheKey,
                entryExpiration,
                policy,
                duration,
                kind: shape.Kind,
                rehydrateAsync: (run, ct) => flow.RehydrateAsync(originalCacheKey, entryExpiration, generator, policy, duration, rehydrateJitter, run, ct),
                entryType: shape.EntryType);
        }

        /// <summary>Reads the inner tier and keeps a hit locally; concurrent reads of one key share one inner read. The null inner tier has nothing to share or keep.</summary>
        public ValueTask<ICacheEntry<TValue>> FetchInnerAsync(TOptions options, CachePolicy policy) =>
            shape.InnerIsNull
                ? shape.ReadInnerAsync(options, policy)
                : FetchSharedInnerAsync(options, policy);

        /// <returns>Whether the value was kept locally; a fill an inner read lost the race for is not.</returns>
        public bool FillFromInnerRead(TOptions options, long mark, TValue value, CachePolicy policy)
        {
            var kept = cache._background.Fill(
                options.CacheKey.Name,
                mark,
                (Cache: cache, Shape: shape, Options: options, Value: value, Max: cache.LocalLifetime(policy), Policy: policy),
                static s => s.Shape.KeepLocal(s.Options, s.Value, s.Max, s.Policy));
            if (kept is null)
            {
                cache.LogInnerCopyNotKept(LoggedKey.For(cache._masker, options.CallerKey, options.CacheKey.Name, shape.EntryType));
            }

            return kept == true;
        }

        /// <summary>Stores and announces the value, or only keeps it locally while the inner tier is disconnected.</summary>
        public async ValueTask<bool> StoreAsync(TOptions options, TValue value, bool disconnected, CachePolicy policy)
        {
            if (disconnected)
            {
                return await InternalSetAsync(options, value, disconnected, policy).ConfigureAwait(false);
            }

            // Written before the event, so a node it wakes reads the new value.
            var (written, kept, counted) = await SetInnerAsync(options, value, disconnected, policy, keepRefused: false).ConfigureAwait(false);
            return written && await PublishSetAsync(options, counted).ConfigureAwait(false) && kept;
        }

        /// <param name="entry">The entry to keep locally, when the caller already built one for <paramref name="value"/>.</param>
        public async ValueTask<bool> InternalSetAsync(TOptions options, TValue value, bool disconnected, CachePolicy policy, bool keepRefused = false, ICacheEntry<TValue>? entry = null) =>
            (await SetInnerAsync(options, value, disconnected, policy, keepRefused, entry).ConfigureAwait(false)).Kept;

        /// <summary>Written and kept apart: a write the inner tier holds is announced either way.</summary>
        public async ValueTask<(bool Written, bool Kept, VersionedWrite Counted)> SetInnerAsync(TOptions options, TValue value, bool disconnected, CachePolicy policy, bool keepRefused, ICacheEntry<TValue>? entry = null)
        {
            try
            {
                if (disconnected)
                {
                    cache.LogSettingLocalOnly(Logged(options));
                    return (false, cache.ApplyLocal(options.CacheKey.Name, (Shape: shape, Options: options, Value: value, Max: policy.LocalExpirationDisconnected ?? cache._multiLayerCacheOptions.LocalMaxExpirationDisconnected, Entry: entry), static s => s.Shape.KeepWhileDisconnected(s.Options, s.Value, s.Max, s.Entry)), default);
                }

                return await cache._background.WriteAsync(
                    options.CacheKey.Name,
                    (Cache: cache, Shape: shape, Options: options, Value: value, Entry: entry, Policy: policy, KeepRefused: keepRefused),
                    null,
                    static (s, keeper) => s.Shape.WriteInnerAsync(s.Options, s.Value, s.Policy, s.KeepRefused, keeper),
                    static (s, written, keeper) => (s.Shape.Written(written), s.Cache.KeepAfterInnerWrite(s.Shape.Written(written), s.KeepRefused, s.Policy, (s.Shape, s.Options, s.Value, s.Entry, Keeper: keeper, Written: written, s.Policy), static (x, max) => x.Shape.KeepWritten(x.Keeper, x.Options, x.Value, max, x.Entry, x.Shape.Version(x.Written), x.Policy), s.Options.Token), s.Shape.Counted(written)),
                    options.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cache.LogInnerCacheSetError(ex, Logged(options));
                return (false, false, default);
            }
        }

        public ValueTask<bool> PublishSetAsync(TOptions options, VersionedWrite counted = default)
        {
            ValueTask<bool> publishing;
            try
            {
                publishing = shape.AnnounceSetAsync(options, counted);
            }
            catch (Exception ex)
            {
                cache.LogSetEventError(ex, Logged(options));
                return new(false);
            }

            // Most events complete at once (broadcast off, or a buffered write): no state machine for those.
            return publishing.IsCompletedSuccessfully ? publishing : AwaitPublishAsync(publishing, options);
        }

        private async ValueTask<bool> AwaitPublishAsync(ValueTask<bool> publishing, TOptions options)
        {
            try
            {
                return await publishing.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cache.LogSetEventError(ex, Logged(options));
                return false;
            }
        }

        private LoggedKey Logged(TOptions options) => LoggedKey.For(cache._masker, options.CallerKey, options.CacheKey.Name, shape.EntryType);

        private async ValueTask<ICacheEntry<TValue>> FetchSharedInnerAsync(TOptions options, CachePolicy policy) =>
            (ICacheEntry<TValue>)(await cache._innerReads.RunAsync(
                new InFlightKey(options.CacheKey.Name, shape.EntryType, policy.LocalExpiration, policy.LocalExpirationDisconnected),
                (Entry: this, Options: options, Policy: policy),
                static (state, run) => state.Entry.FetchAndKeepAsync(state.Options.Token == run.Token ? state.Options : state.Options.WithToken(run.Token), state.Policy, run),
                options.Token).ConfigureAwait(false))!;

        private async ValueTask<object?> FetchAndKeepAsync(TOptions options, CachePolicy policy, IInFlightRun run)
        {
            var mark = cache.MarkInnerRead(options.CacheKey.Name, options.TopicKey);
            var fetched = await shape.ReadInnerAsync(options, policy).ConfigureAwait(false);
            if (fetched.Found)
            {
                // Committed only while a caller still waits, so a read every caller left cannot overwrite a fresh one.
                run.TryCommit((Entry: this, Options: options, Policy: policy, Fetched: fetched, Mark: mark), static s => s.Entry.Keep(s.Options, s.Policy, s.Fetched, s.Mark));
            }

            return fetched;
        }

        private bool Keep(TOptions options, CachePolicy policy, ICacheEntry<TValue> fetched, long mark)
        {
            shape.LogFoundInner(options);
            shape.Adopt(ref options, fetched);
            return FillFromInnerRead(options, mark, shape.Present(fetched.Value), policy);
        }

        private async ValueTask RehydrateAsync(CacheKey originalCacheKey, DateTimeOffset entryExpiration, Func<CancellationToken, Task<TValue>> generator, CachePolicy policy, TimeSpan duration, TimeSpan? rehydrateJitter, RehydrateRun run, CancellationToken ct)
        {
            if (await TryAdoptInnerAsync(originalCacheKey, entryExpiration, policy, duration, run, ct).ConfigureAwait(false))
            {
                return;
            }

            var newValue = await generator(ct).ConfigureAwait(false);
            var empty = shape.IsEmpty(newValue);
            if (empty && !cache._multiLayerCacheOptions.CacheNullValues)
            {
                return;
            }
            // Factory transitions to empty: preserve the original deadline so the marker doesn't get a fresh TTL window.
            var rehydrateExpiration = empty
                ? entryExpiration
                : cache._clock.ToDateTimeOffset(ApplyJitter(duration, rehydrateJitter));
            var rehydrateOptions = shape.BuildOptions(originalCacheKey, rehydrateExpiration, ct);
            if (!await StoreAsync(rehydrateOptions, shape.Present(newValue), cache.GetInnerCacheDisconnected(), policy).ConfigureAwait(false))
            {
                throw new RehydrateWriteFailedException(originalCacheKey.Name);
            }
        }

        /// <summary>Keeps the inner tier's copy when another node already refreshed it, so the generator is not run again; true only when the copy was kept.</summary>
        private async ValueTask<bool> TryAdoptInnerAsync(CacheKey originalCacheKey, DateTimeOffset entryExpiration, CachePolicy policy, TimeSpan duration, RehydrateRun run, CancellationToken ct)
        {
            if (shape.InnerIsNull || !shape.CanAdopt || cache.GetInnerCacheDisconnected())
            {
                return false;
            }

            var rehydrate = policy.Rehydrate!;
            var options = shape.BuildOptions(originalCacheKey, entryExpiration, ct);
            using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var budget = RehydrationCoordinator.AdoptReadTimeout(rehydrate, duration);
            read.CancelAfter(budget);
            var mark = cache.MarkInnerRead(options.CacheKey.Name, options.TopicKey);
            Task<ICacheEntry<TValue>>? reading = null;
            ICacheEntry<TValue> fetched;
            try
            {
                reading = shape.ReadForAdoptionAsync(options.WithToken(read.Token)).AsTask();
                // The token alone does not bound a read that ignores it; an abandoned read is never kept, as nothing below runs for it.
                fetched = await reading.WaitAsync(budget, cache._clock, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                read.Cancel();
                reading?.Observe();
                var timedOut = ex is TimeoutException or OperationCanceledException;
                var logged = LoggedKey.For(cache._masker, originalCacheKey, shape.EntryType);
                if (timedOut)
                {
                    cache.LogRehydrateInnerReadTimedOut(logged);
                }
                else
                {
                    cache.LogRehydrateInnerReadFailed(ex, logged);
                }

                cache._rehydrator.TrackAdoptReadFailed(rehydrate, shape.Kind, originalCacheKey, timedOut ? "timeout" : "error");
                return false;
            }

            ct.ThrowIfCancellationRequested();
            if (!fetched.Found || !InnerCopyFreshEnough(fetched.Expiration, cache._clock.GetUtcNow(), rehydrate.Threshold, duration))
            {
                return false;
            }

            try
            {
                if (!Keep(options, policy, fetched, mark))
                {
                    return false;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                cache.LogRehydrateAdoptFailed(ex, LoggedKey.For(cache._masker, originalCacheKey, shape.EntryType));
                return false;
            }

            run.Adopt(originalCacheKey);
            return true;
        }
    }
}
