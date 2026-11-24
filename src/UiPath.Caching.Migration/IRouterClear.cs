namespace UiPath.Caching.Migration;

/// <summary>A routed cache that the router can clear by name, whatever its value type.</summary>
internal interface IRouterClear
{
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}
