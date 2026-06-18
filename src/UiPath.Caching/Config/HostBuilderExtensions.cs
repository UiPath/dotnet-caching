using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Config;

[ExcludeFromCodeCoverage]
public static class HostBuilderExtensions
{
    /// <summary>
    /// Adds a delegate for configuring the provided <see cref="ICachingBuilder"/>. This may be called multiple times.
    /// </summary>
    /// <param name="hostBuilder">The <see cref="IHostBuilder" /> to configure.</param>
    /// <param name="configureCaching">The delegate that configures the <see cref="ICachingBuilder"/>.</param>
    /// <returns>The same instance of the <see cref="IHostBuilder"/> for chaining.</returns>
    public static IHostBuilder ConfigureCaching(this IHostBuilder hostBuilder, Action<ICachingBuilder> configureCaching) =>
        hostBuilder.ConfigureServices((context, collection) => collection.AddCaching(context.Configuration, builder => configureCaching(builder)));
}
