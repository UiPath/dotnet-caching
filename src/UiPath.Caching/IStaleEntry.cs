namespace UiPath.Caching;

/// <summary>A value fail-safe returned in place of a generation: never stored, and reported as not generated and with no live expiration.</summary>
internal interface IStaleEntry
{
    /// <summary>The kept value the run fell back to; a caller that joined the run checks it is still kept, and within its own stale age.</summary>
    StaleValue Kept { get; }

    /// <summary>What the run would have answered without fail-safe, for a caller that joined it without opting in; <paramref name="token"/> ends that caller's wait for a late generator.</summary>
    Task<object?> GeneratedAsync(CancellationToken token);
}
