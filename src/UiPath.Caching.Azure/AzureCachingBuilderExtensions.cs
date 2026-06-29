using UiPath.Caching.Azure;

namespace UiPath.Caching.Config;

/// <summary>Builder extensions for adding Azure Entra ID authentication to a caching setup.</summary>
[ExcludeFromCodeCoverage]
public static class AzureCachingBuilderExtensions
{
    private const string DefaultSectionName = "AzureEntra";

    /// <summary>Registers Entra ID authentication for the Redis connection. Token refresh is automatic.</summary>
    public static ICachingBuilder AddAzureEntraAuthentication(this ICachingBuilder builder, Action<AzureEntraOptions>? configure = null) =>
        builder.AddAzureEntraAuthentication(DefaultSectionName, configure);

    /// <summary>Binds <see cref="AzureEntraOptions"/> from the given configuration section, then registers Entra auth.</summary>
    public static ICachingBuilder AddAzureEntraAuthentication(this ICachingBuilder builder, string sectionName) =>
        builder.AddAzureEntraAuthentication(sectionName, configure: null);

    private static ICachingBuilder AddAzureEntraAuthentication(this ICachingBuilder builder, string sectionName, Action<AzureEntraOptions>? configure)
    {
        builder.Services.AddOptions<AzureEntraOptions>();
        builder.Services.Configure<AzureEntraOptions>(opt =>
        {
            builder.Configuration.GetSection(sectionName).Bind(opt);
            configure?.Invoke(opt);
        });

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IRedisConnectionConfigurator, AzureEntraConnectionConfigurator>());
        return builder;
    }
}
