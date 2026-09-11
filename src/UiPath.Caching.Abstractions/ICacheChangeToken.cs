using Microsoft.Extensions.Primitives;

namespace UiPath.Caching;

public interface ICacheChangeToken : IChangeToken
{

    IDictionary<string, string?>? Metadata { get; }
    bool MetadataHasChanged { get; }

    DateTimeOffset? Expiration { get; }

    string? TransportId { get; }
}
