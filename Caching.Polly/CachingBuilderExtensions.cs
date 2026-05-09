using Polly.Telemetry;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private const string DefaultSectionName = "ResiliencePolicies";

    public static ICachingBuilder AddResilienceStrategies(this ICachingBuilder builder) =>
        builder.AddResilienceStrategies(DefaultSectionName);

    public static ICachingBuilder AddResilienceStrategies(this ICachingBuilder builder, string sectionName) =>
        builder.AddResilienceStrategies(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddResilienceStrategies(this ICachingBuilder builder,
        Action<ResiliencePoliciesOptions> configureOptions,
        Action<TelemetryOptions>? configureTelemetryOptions = null)
    {
        ResiliencePoliciesOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        if (!builder.Enabled || !options.Enabled)
        {
            return builder;
        }

        if (options.TelemetryEnabled && configureTelemetryOptions is not null)
        {
            builder.Services.Configure(configureTelemetryOptions);
        }

        builder.Services.AddSingleton<IResiliencePipelineFactory>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            var resilienceOptions = sp.GetRequiredService<IOptions<ResiliencePoliciesOptions>>();
            TelemetryOptions? telemetryOptions = resilienceOptions.Value.TelemetryEnabled
                ? sp.GetRequiredService<IOptions<TelemetryOptions>>().Value
                : null;

            return new ResiliencePipelineFactory(loggerFactory, telemetryOptions, resilienceOptions);
        });

        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        builder.RegisterOnCompleteCallback(typeof(CachingBuilderExtensions), b =>
            b.Services.TryAddSingleton<IResiliencePipelineHolder>(sp => sp.BuildResiliencePipelineHolder(b)));

        return builder;
    }

    private static ResiliencePipelineHolder BuildResiliencePipelineHolder(this IServiceProvider serviceProvider, ICachingBuilder builder)
    {
        if (!builder.Enabled)
        {
            return ResiliencePipelineHolder.Empty;
        }

        var factory = serviceProvider.GetRequiredService<IResiliencePipelineFactory>();

        return new ResiliencePipelineHolder(new ResiliencePipelineWrapper(factory, "read"), new ResiliencePipelineWrapper(factory, "write"));
    }
}
