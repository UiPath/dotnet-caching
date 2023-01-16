using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching;

public interface IExtendedPropertiesChangeToken : IChangeToken
{
    bool ExtendedPropertiesHasChanged { get; }
}
