namespace UiPath.Caching.KeyMove;

/// <summary>The parts of a Clear that belong to the library's cache: its two events and the sweep of L and N for the cache.</summary>
internal sealed record ClearHooks(Func<CancellationToken, Task> PublishFirst, Func<CancellationToken, Task> Sweep, Func<CancellationToken, Task> PublishSecond);
