using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching;

public interface ICacheChangeToken : IChangeToken
{
    bool MetadataHasChanged { get; }

    DateTimeOffset? Expiration { get; }

    public IDictionary<string, string?>? Metadata { get; }
}
