using Polly.Telemetry;
using UiPath.Caching.Policies;

namespace UiPath.Caching.Polly;

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
            var resilienceOptions = sp.GetRequiredService<IOptionsMonitor<ResiliencePoliciesOptions>>();
            TelemetryOptions? telemetryOptions = resilienceOptions.CurrentValue.TelemetryEnabled
                ? sp.GetRequiredService<IOptions<TelemetryOptions>>().Value
                : null;

            return new ResiliencePipelineFactory(loggerFactory, telemetryOptions, resilienceOptions);
        });

        // Predefined pipelines, seeded with the same base configuration.
        builder.AddResiliencePipeline(ResiliencePipelineNames.Read, configureOptions);
        builder.AddResiliencePipeline(ResiliencePipelineNames.Write, configureOptions);

        return builder.AddCallback();
    }

    /// <summary>
    /// Registers a named resilience pipeline. The <paramref name="name"/> is the scope passed to
    /// <see cref="IResiliencePipelineProvider.Get"/>; <paramref name="configureOptions"/> configures
    /// the <see cref="ResiliencePoliciesOptions"/> used to build that pipeline. Call this after
    /// <see cref="AddResilienceStrategies(ICachingBuilder, Action{ResiliencePoliciesOptions}, Action{TelemetryOptions})"/>;
    /// it can also retune the predefined <see cref="ResiliencePipelineNames.Read"/> /
    /// <see cref="ResiliencePipelineNames.Write"/> pipelines.
    /// </summary>
    public static ICachingBuilder AddResiliencePipeline(this ICachingBuilder builder, string name, Action<ResiliencePoliciesOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configureOptions);

        GetOrAddRegistry(builder.Services).Add(name);
        builder.Services.Configure(name, configureOptions);
        return builder;
    }

    private static ResiliencePipelineRegistry GetOrAddRegistry(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(ResiliencePipelineRegistry))?.ImplementationInstance is ResiliencePipelineRegistry existing)
        {
            return existing;
        }

        ResiliencePipelineRegistry registry = new();
        services.AddSingleton(registry);
        return registry;
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        builder.RegisterOnCompleteCallback(typeof(CachingBuilderExtensions), b =>
            b.Services.TryAddSingleton<IResiliencePipelineProvider>(sp => sp.BuildResiliencePipelineProvider(b)));

        return builder;
    }

    private static IResiliencePipelineProvider BuildResiliencePipelineProvider(this IServiceProvider serviceProvider, ICachingBuilder builder)
    {
        if (!builder.Enabled)
        {
            return EmptyResiliencePipelineProvider.Instance;
        }

        var factory = serviceProvider.GetRequiredService<IResiliencePipelineFactory>();
        var registry = serviceProvider.GetRequiredService<ResiliencePipelineRegistry>();

        return new ResiliencePipelineProvider(factory, registry);
    }
}
