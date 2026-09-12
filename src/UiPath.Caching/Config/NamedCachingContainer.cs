using System.Text.Json;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Config;

/// <summary>The stack's own container, disposed with the application's.</summary>
internal sealed class NamedCachingContainer : IDisposable
{
    internal const string PrimaryConnection = "Redis";

    private NamedCachingContainer(string name, ServiceProvider provider)
    {
        Name = name;
        Provider = provider;
    }

    public string Name { get; }

    public ServiceProvider Provider { get; }

    public static NamedCachingContainer Of(IServiceProvider root, string name) =>
        root.GetRequiredKeyedService<NamedCachingContainer>(name);

    public static NamedCachingContainer Build(
        string name,
        IServiceProvider root,
        IConfigurationSection section,
        Action<ICachingBuilder> configure,
        Action<RedisConnectionOptions>? configureConnection,
        Action<IServiceCollection, IServiceProvider>? configureServices)
    {
        // Without one there is no key casing to match, and two stacks would seed the process-wide default against each other.
        if (root.GetService<ICacheFactory>() is null)
        {
            throw new InvalidOperationException(
                $"Named caching stack '{name}' found no caching on the application's container. Call AddCaching there as well: " +
                "a named stack is a second stack beside it, not a replacement for it.");
        }

        var applicationCasing = root.GetRequiredService<IOptions<CacheOptions>>().Value.KeyCasing;
        ThrowIfConfiguredKeyCasingDiffers(name, applicationCasing, section);

        var services = new ServiceCollection();

        // Shared with the application; nothing Redis-facing and no memory cache is, so each stack keeps its own.
        services.AddSingleton(root.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        Forward<TimeProvider>(services, root);
        Forward<ICachingTelemetryProvider>(services, root);
        Forward<IKeyMaskingPolicy>(services, root);
        Forward<JsonSerializerOptions>(services, root);
        configureServices?.Invoke(services, root);

        services.AddCaching(
            section,
            builder =>
            {
                AddConnection(builder, name, configureConnection);
                var connectionConfigurers = CountConnectionConfigurers(builder.Services);
                configure(builder);
                if (CountConnectionConfigurers(builder.Services) != connectionConfigurers)
                {
                    throw new InvalidOperationException(
                        $"The chain of named caching stack '{name}' configured the Redis connection itself. The stack's connection is " +
                        $"'{section.Path}:{ConnectionSection(name)}' laid over '{section.Path}:{ConnectionSection(PrimaryConnection)}'; " +
                        "remove AddRedisConnection from the chain, or pass configureConnection for settings that need code.");
                }
            },
            options => section.Bind(options));

        // Left in, it would publish a casing the check below is about to refuse, for other threads to read meanwhile.
        RemoveKeyCasingSeeder(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        try
        {
            ThrowIfEffectiveKeyCasingDiffers(name, applicationCasing, provider);
            ThrowIfConnectionIsNotItsOwn(name, root, provider);
        }
        catch
        {
            provider.Dispose();
            throw;
        }

        return new NamedCachingContainer(name, provider);
    }

    public void Dispose() => Provider.Dispose();

    internal static string ConnectionSection(string connectionName) => $"Connections:{connectionName}";

    /// <summary>Both sections are bound in order, so a key the stack leaves unset keeps the primary connection's value.</summary>
    private static void AddConnection(ICachingBuilder builder, string name, Action<RedisConnectionOptions>? configureConnection) =>
        builder.AddRedisConnection(ConnectionSection(PrimaryConnection), options =>
        {
            builder.Configuration.GetSection(ConnectionSection(name)).Bind(options);
            configureConnection?.Invoke(options);
        });

    /// <summary>Both sides are the resolved options; neither message names a value, which can carry credentials.</summary>
    private static void ThrowIfConnectionIsNotItsOwn(string name, IServiceProvider root, IServiceProvider stack)
    {
        var own = stack.GetService<IOptions<RedisConnectionOptions>>()?.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(own))
        {
            throw new InvalidOperationException(
                $"Named caching stack '{name}' resolves to an empty ConnectionString. Set one under " +
                $"'{ConnectionSection(name)}', or from code through configureConnection.");
        }

        var primary = root.GetService<IOptions<RedisConnectionOptions>>()?.Value.ConnectionString;
        if (!string.IsNullOrWhiteSpace(primary) && string.Equals(primary, own, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Named caching stack '{name}' resolves to the same ConnectionString as the application's primary connection. " +
                $"Give '{ConnectionSection(name)}' its own ConnectionString.");
        }
    }

    private static int CountConnectionConfigurers(IServiceCollection services) =>
        services.Count(d => d.ServiceType == typeof(IConfigureOptions<RedisConnectionOptions>));

    /// <summary>Runs before the stack's AddCaching, so a casing from configuration is refused before anything seeds it.</summary>
    private static void ThrowIfConfiguredKeyCasingDiffers(string name, CacheKeyCasing casing, IConfigurationSection section)
    {
        var options = new CacheOptions();
        section.Bind(options);
        if (casing != options.KeyCasing)
        {
            throw new InvalidOperationException(
                $"Named caching stack '{name}' binds KeyCasing {options.KeyCasing} from '{section.Path}', while the application's caching uses {casing}. " +
                "Key casing is process-wide (CacheKey.DefaultCasing), so every stack must use the same value.");
        }
    }

    /// <summary>Catches what a callback set past the configured check; with the seeder gone, reading it publishes nothing.</summary>
    private static void ThrowIfEffectiveKeyCasingDiffers(string name, CacheKeyCasing casing, IServiceProvider stack)
    {
        var own = stack.GetRequiredService<IOptions<CacheOptions>>().Value.KeyCasing;
        if (own != casing)
        {
            throw new InvalidOperationException(
                $"Named caching stack '{name}' resolves KeyCasing {own}, while the application's caching uses {casing}. " +
                "Key casing is process-wide (CacheKey.DefaultCasing), so every stack must use the same value.");
        }
    }

    private static void RemoveKeyCasingSeeder(IServiceCollection services)
    {
        var seeder = services.FirstOrDefault(descriptor =>
            descriptor.ServiceType == typeof(IValidateOptions<CacheOptions>) && descriptor.ImplementationType == typeof(CacheKeyCasingSeeder));
        if (seeder is not null)
        {
            services.Remove(seeder);
        }
    }

    private static void Forward<TService>(IServiceCollection services, IServiceProvider root)
        where TService : class
    {
        if (root.GetService<TService>() is { } instance)
        {
            services.AddSingleton(instance);
        }
    }
}
