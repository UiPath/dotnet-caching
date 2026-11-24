namespace UiPath.Caching.Migration;

/// <summary>Whether the caller of a promotion still owns the work: the mover's unit lease for as long as one key's promotion lasts.</summary>
internal interface IMoveOwnership
{
    /// <summary>The same judgement with no I/O: the lease's local validity and the last known state. For the loops that retry an attempt, which check it before each one.</summary>
    bool StillHeld { get; }

    /// <summary>True while the lease is locally valid and the state is still the runner's, renewing the lease when it is due; false means stop at once and write nothing more.</summary>
    ValueTask<bool> HoldsAsync(CancellationToken cancellationToken);

    /// <summary>Records the bytes of a publish that was sent, once for each key, with no wait: the runner pays the debt before it captures the next key, so nothing waits while a fence or a marker is held.</summary>
    void Published(long bytes);
}
