using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching;

public interface ICacheChangeToken : IChangeToken
{
    bool MetadataHasChanged { get; }

    DateTimeOffset? Expiration { get; }

    string? TransportId { get; }

    public IDictionary<string, string?>? Metadata { get; }
}
