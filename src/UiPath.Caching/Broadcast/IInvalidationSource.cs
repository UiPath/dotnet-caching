namespace UiPath.Caching.Broadcast;

internal interface IInvalidationSource
{
    /// <summary>Registers the one callback told, before the token changes, that an event or a loss is changing it; the flag is true for a loss, which names no key.</summary>
    void RegisterInvalidatingCallback(Action<object?, bool> callback, object? state);
}
