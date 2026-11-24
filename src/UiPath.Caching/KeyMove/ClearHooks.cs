namespace UiPath.Caching.KeyMove;

/// <summary>The parts of a Clear that belong to the library's cache: its two events and the sweep of L and N for the cache.</summary>
internal sealed record ClearHooks(Func<CancellationToken, Task> PublishFirst, Func<CancellationToken, Task> Sweep, Func<CancellationToken, Task> PublishSecond)
{
    /// <summary>The sweep that also asks, before each batch it deletes, whether the Clear's lock has lapsed (its argument); when set it runs instead of <see cref="Sweep"/>.</summary>
    public Func<Func<bool>, CancellationToken, Task>? GuardedSweep { get; init; }
}
