using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace UiPath.Caching.Config;
#nullable disable

[ExcludeFromCodeCoverage]
public class NullConfiguration : IConfiguration
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

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => NullChangeToken.Singleton;

    public IConfigurationSection GetSection(string key) => NullConfigurationSection.Instance;
}
