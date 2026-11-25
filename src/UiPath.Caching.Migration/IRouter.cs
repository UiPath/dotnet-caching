namespace UiPath.Caching.Migration;

public interface IRouter
{
    /// <summary>Raised after a transition has drained, with the mode now in force.</summary>
    event Action<RouterMode, RouterModeReason>? ModeChanged;

    /// <summary>The mode new calls start in; a call that started before a change finishes in the mode it started in.</summary>
    RouterMode Mode { get; }

    RouterModeReason ModeReason { get; }

    /// <summary>The routed cache as the library's own <see cref="ICache{T}"/>. While the source serves the group, <c>TryAdd</c>, <c>TryFill</c>, <c>SetIfChanged</c>, <c>Refresh</c>, <c>TimeToLive</c> and <c>ExpireTime</c> throw <see cref="NotSupportedException"/>, as the source has none; while the group's keys move, <c>SetIfChanged</c> and <c>Refresh</c> do. Every other member works on every path.</summary>
    ICache<T> Get<T>(string cacheName)
        where T : class;

    /// <summary>The routed hash cache as the library's own <see cref="IHashCache{T}"/>. A metadata write throws <see cref="NotSupportedException"/> on every path. While the source serves the group, <c>Refresh</c>, <c>TimeToLive</c> and <c>ExpireTime</c> throw it too, as the source has none, and a write that carries metadata is refused; every other member works on every path.</summary>
    IHashCache<T> GetHash<T>(string cacheName)
        where T : class;

    /// <summary>Clears one routed cache by name, on every path; refused while its group's keys move, which has its own clear.</summary>
    Task ClearAsync(string cacheName, CancellationToken cancellationToken = default);

    /// <summary>Puts every call on the source path and returns once calls started in library mode have ended, or the source timeout has passed.</summary>
    Task EnterBypassAsync(CancellationToken cancellationToken = default);

    /// <summary>Withdraws the explicit request and leaves bypass once nothing else asks for it; throws TimeoutException if the prerequisites do not settle in time.</summary>
    Task LeaveBypassAsync(CancellationToken cancellationToken = default);
}
