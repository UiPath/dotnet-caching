using Polly.Telemetry;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private const string DefaultSectionName = "ResiliencePolicies";

    private static bool _telemetryEnabled = false;
    private static Action<TelemetryOptions>? _telemetryConfig;
    private static int _callbackRegistered = 0;

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

        builder.Services.AddSingleton<IResiliencePipelineFactory>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            TelemetryOptions? telemetryOptions = null;
            if (_telemetryEnabled)
            {
                telemetryOptions = new TelemetryOptions();
                _telemetryConfig?.Invoke(telemetryOptions);
             }

            return new ResiliencePipelineFactory(loggerFactory, telemetryOptions, sp.GetRequiredService<IOptions<ResiliencePoliciesOptions>>());
        });

        return builder
            .ConfigureTelemetry(enabled: options.TelemetryEnabled, configureTelemetryOptions)
            .AddCallback();
    }


    public static ICachingBuilder ConfigureTelemetry(this ICachingBuilder builder, bool enabled = true, Action<TelemetryOptions>? configureOptions = null)
    {
        _telemetryEnabled = enabled;
        _telemetryConfig = enabled ? configureOptions : null;
        return builder;
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        if (Interlocked.Exchange(ref _callbackRegistered, 1) == 0)
        {
            builder.RegisterOnCompleteCallback(builder => builder.Services.TryAddSingleton<IResiliencePipelineHolder>(sp => sp.BuildResiliencePipelineHolder(builder)));
        }

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
