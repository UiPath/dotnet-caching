using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Config;

[CollectionDefinition("CacheKeyDefaultCasing", DisableParallelization = true)]
public class CacheKeyDefaultCasingCollection;

[Collection("CacheKeyDefaultCasing")]
public class KeyCasingOptionsTests
{
    [Fact]
    public void KeyCasing_defaults_to_insensitive()
    {
        new CacheOptions().KeyCasing.Should().Be(CacheKeyCasing.Insensitive);
    }

    [Fact]
    public void Resolving_options_seeds_the_ambient_default()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddCaching(_ => { }, o => o.KeyCasing = CacheKeyCasing.Sensitive);
            using var provider = services.BuildServiceProvider();

            _ = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

            CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Sensitive);
            new CacheKey("AbC").Name.Should().Be("AbC");
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }

    [Fact]
    public void Insensitive_configuration_keeps_lowercasing()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddCaching(_ => { });
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<IOptions<CacheOptions>>().Value;

            CacheKey.DefaultCasing.Should().Be(CacheKeyCasing.Insensitive);
            new CacheKey("AbC").Name.Should().Be("abc");
        }
        finally
        {
            CacheKey.DefaultCasing = CacheKeyCasing.Insensitive;
        }
    }
}
