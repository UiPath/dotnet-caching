
namespace UiPath.Caching.Broadcast;

[ExcludeFromCodeCoverage]
public class NullCacheChangeToken : ICacheChangeToken
{

    private NullCacheChangeToken()
    {
    }
    public static NullCacheChangeToken Instance { get; } = new NullCacheChangeToken();

    public bool HasChanged => false;

    public bool ActiveChangeCallbacks => false;

    public bool MetadataHasChanged => false;

    public DateTimeOffset? Expiration => null;

    public IDictionary<string, string?>? Metadata => null;

    public string? TransportId => null;

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
        Disposable.Empty;
}
