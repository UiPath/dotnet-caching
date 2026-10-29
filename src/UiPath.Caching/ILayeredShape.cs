namespace UiPath.Caching;

/// <summary>What differs between the entries of the plain and the hash multilayer caches; the flow they share is the layered entry flow of <see cref="MultilayerCacheBase"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2436:Types and methods should not have too many generic parameters", Justification = "A struct shape keeps the shared flow free of virtual calls; C# has no associated types, so the value, options and write result are type parameters.")]
internal interface ILayeredShape<TValue, TOptions, TWrite>
    where TOptions : struct, ILayeredOptions<TOptions>
{
    /// <summary>The value type the telemetry names.</summary>
    Type EntryType { get; }

    /// <summary>The kind of cache the telemetry and logs name: <c>cache</c> or <c>hash</c>.</summary>
    string Kind { get; }

    /// <summary>Whether the inner tier is the null one, which has nothing to share or keep.</summary>
    bool InnerIsNull { get; }

    /// <summary>Whether the value is nothing a caller could use: null for a plain entry, null or no fields for a hash.</summary>
    bool IsEmpty(TValue? value);

    /// <summary>The value to store for one the generator produced: a hash stores no null.</summary>
    TValue Present(TValue? value);

    TOptions BuildOptions(CacheKey cacheKey, DateTimeOffset expiration, CancellationToken token);

    ValueTask<ICacheEntry<TValue>> ReadInnerAsync(TOptions options, CachePolicy policy);

    /// <summary>Takes what the inner tier holds into the options of the local copy: its deadline, and what only this kind of entry has.</summary>
    void Adopt(ref TOptions options, ICacheEntry<TValue> fetched);

    void LogFoundInner(TOptions options);

    /// <summary>Writes the value to the local tier, no longer than <paramref name="maxExpiration"/>.</summary>
    bool KeepLocal(TOptions options, TValue value, TimeSpan? maxExpiration);

    /// <summary>Writes the value to the inner tier; a write that throws counts as refused, not as failed, only when <paramref name="keepRefused"/>.</summary>
    ValueTask<TWrite> WriteInnerAsync(TOptions options, TValue value, CachePolicy policy, bool keepRefused, BackgroundWrites.Keeper keeper);

    /// <summary>Whether the inner tier took the write.</summary>
    bool Written(TWrite write);

    /// <summary>What the write counted for the event that announces it.</summary>
    VersionedWrite Counted(TWrite write);

    /// <summary>The version the local copy stands on after the write.</summary>
    long Version(TWrite write);

    /// <summary>Keeps the value locally while the inner tier is disconnected.</summary>
    bool KeepWhileDisconnected(TOptions options, TValue value, TimeSpan? maxExpiration, ICacheEntry<TValue>? entry);

    /// <summary>Keeps the value locally after an inner write, as the write's turn allows.</summary>
    bool KeepWritten(BackgroundWrites.Keeper keeper, TOptions options, TValue value, TimeSpan? maxExpiration, ICacheEntry<TValue>? entry, long version);

    /// <summary>Starts the event that announces the write; may throw.</summary>
    ValueTask<bool> AnnounceSetAsync(TOptions options, VersionedWrite counted);
}
