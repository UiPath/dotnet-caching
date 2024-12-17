using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace UiPath.Platform.Caching.Config;
#nullable disable

[ExcludeFromCodeCoverage]
public class NullConfigurationSection : IConfigurationSection
{
    public static readonly IConfigurationSection Instance = new NullConfigurationSection();

    public string Key => null;

    public string Path => null;

    public string Value { get; set; }

    public string this[string key]
    {
        get => null;
        set
        {
            //do nothing
        }
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IConfigurationSection GetSection(string key) => Instance;

    public IChangeToken GetReloadToken() => NullChangeToken.Singleton;
}
