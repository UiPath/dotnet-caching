namespace UiPath.Caching.Migration;

public interface IRouter
{
    /// <summary>Raised after a transition has drained, with the mode now in force.</summary>
    event Action<RouterMode, RouterModeReason>? ModeChanged;

    /// <summary>The mode new calls start in; a call that started before a change finishes in the mode it started in.</summary>
    RouterMode Mode { get; }

    RouterModeReason ModeReason { get; }

    IRoutedCache<T> Get<T>(string cacheName)
        where T : class;

    /// <summary>Puts every call on the source path and returns once calls started in library mode have ended, or the source timeout has passed.</summary>
    Task EnterBypassAsync(CancellationToken cancellationToken = default);

    /// <summary>Withdraws the explicit request and leaves bypass once nothing else asks for it; throws TimeoutException if the prerequisites do not settle in time.</summary>
    Task LeaveBypassAsync(CancellationToken cancellationToken = default);
}
