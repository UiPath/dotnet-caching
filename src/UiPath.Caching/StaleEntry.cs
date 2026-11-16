using System.Runtime.ExceptionServices;

namespace UiPath.Caching;

/// <summary>A value fail-safe returned in place of a generation: never stored, and reported as not generated and with no live expiration.</summary>
/// <param name="failure">The generator's error, when fail-safe answered it.</param>
/// <param name="late">The generator's own answer, when fail-safe stopped waiting for it.</param>
internal sealed class StaleEntry<T>(StaleValue value, ICacheEntry<T?> kept, Exception? failure = null, Task<ICacheEntry<T?>>? late = null) : ICacheEntry<T?>, IStaleEntry
{
    public T? Value => kept.Value;

    public DateTimeOffset Expiration => kept.Expiration;

    public IDictionary<string, string?>? Metadata => kept.Metadata;

    public StaleValue Kept => value;

    object? ICacheEntry.Value => kept.Value;

    public bool Found => true;

    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) => kept.NewEntry(expiration, metadata);

    /// <summary>What the run would have answered without fail-safe: its generator's error, or its generator's value once it ends.</summary>
    public async Task<object?> GeneratedAsync(CancellationToken token)
    {
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return late is null ? kept : await late.WaitAsync(token).ConfigureAwait(false);
    }
}
