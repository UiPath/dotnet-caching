using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;

namespace UiPath.Caching.Tests.Config;

/// <summary><see cref="CacheKey.DefaultCasing"/> is process-wide, so these join the collection that serializes such tests.</summary>
[Collection("CacheKeyDefaultCasing")]
public class NamedCachingKeyCasingTests
{
    private const string Name = "SecondaryRedis";

    [Fact]
    public void A_key_casing_declared_in_configuration_is_refused_before_the_global_default_moves()
    {
        var casing = CacheKey.DefaultCasing;
        using var root = Build(configureChild: null, ("CachingCold:KeyCasing", nameof(CacheKeyCasing.Sensitive)));

        var act = () => root.GetRequiredKeyedService<ICacheFactory>("cold");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Sensitive*Insensitive*");
        CacheKey.DefaultCasing.Should().Be(casing);
    }

    /// <summary>A callback sets it past the configured check, so only the removed seeder keeps it off the global default.</summary>
    [Fact]
    public void A_key_casing_set_by_a_callback_is_refused_without_the_global_default_moving()
    {
        var casing = CacheKey.DefaultCasing;
        using var root = Build(configureChild: child => child.Configure<CacheOptions>(o => o.KeyCasing = CacheKeyCasing.Sensitive));

        var act = () => root.GetRequiredKeyedService<ICacheFactory>("cold");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Sensitive*Insensitive*");
        CacheKey.DefaultCasing.Should().Be(casing);
    }

    [Fact]
    public void A_matching_key_casing_is_accepted()
    {
        var casing = CacheKey.DefaultCasing;
        using var root = Build(configureChild: null, ("CachingCold:KeyCasing", nameof(CacheKeyCasing.Insensitive)));

        root.GetRequiredKeyedService<ICacheFactory>("cold").Should().NotBeNull();

        CacheKey.DefaultCasing.Should().Be(casing);
    }

    private static ServiceProvider Build(Action<IServiceCollection>? configureChild = null, params (string Path, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Caching:AppShortName"] = "app",
            ["Caching:KeyCasing"] = nameof(CacheKeyCasing.Insensitive),
            ["Caching:Connections:Redis:ConnectionString"] = "primary:6379",
            ["Caching:Connections:Redis:WarmUpOnStart"] = "false",
            ["Caching:Connections:Redis:PlannedMaintenanceEnabled"] = "false",
            ["CachingCold:AppShortName"] = "app",
            ["CachingCold:Connections:cold:ConnectionString"] = "cold:6381",
        };
        foreach (var (path, value) in settings)
        {
            values[path] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCaching(configuration, b => b.AddRedisConnection().AddRedis());
        services.AddNamedCaching(
            "cold",
            configuration,
            b => b.AddRedis(),
            sectionName: "CachingCold",
            configureServices: (child, _) => configureChild?.Invoke(child));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
