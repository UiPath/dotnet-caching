using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching.Config;
#nullable disable

internal class NullConfiguration : IConfiguration
{
    public static readonly IConfiguration Instance = new NullConfiguration();

    public string this[string key]
    {
        get => null;
        set
        {
            //do nothing
        }
    }

    public IEnumerable<IConfigurationSection> GetChildren() => Enumerable.Empty<IConfigurationSection>();

    public IChangeToken GetReloadToken() => NullChangeToken.Singleton;

    public IConfigurationSection GetSection(string key) => null;
}
