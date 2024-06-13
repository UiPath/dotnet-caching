using Polly.Telemetry;
using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private const string DefaultSectionName = "ResiliencePolicies";

    private static readonly List<Action<IServiceProvider, ResiliencePipelineBuilder>> ReadStrategies = [];
    private static readonly List<Action<IServiceProvider, ResiliencePipelineBuilder>> WriteStrategies = [];
    private static bool _telemetryEnabled = false;
    private static Action<TelemetryOptions>? _telemetryConfig;

    public static ICachingBuilder AddResilienceStrategies(this ICachingBuilder builder) =>
        builder.AddResilienceStrategies(DefaultSectionName);

    public static ICachingBuilder AddResilienceStrategies(this ICachingBuilder builder, string sectionName) =>
        builder.AddResilienceStrategies(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddResilienceStrategies(this ICachingBuilder builder, Action<ResiliencePoliciesOptions> configureOptions)
    {
        ResiliencePoliciesOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        ReadStrategies.Add((sp, builder) => sp.AddCircuitBreaker(builder, options));
        ReadStrategies.Add((sp, builder) => sp.AddRetryPolicy(builder, options));
        ReadStrategies.Add((sp, builder) => sp.AddTimeoutPolicy(builder, options));
        return builder
            .ConfigureTelemetry(enabled: options.TelemetryEnabled)
            .AddCallback();
    }

    public static ICachingBuilder AddReadStrategy(this ICachingBuilder builder, Action<IServiceProvider, ResiliencePipelineBuilder> configure)
    {
        ReadStrategies.Add(configure);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddWriteStrategy(this ICachingBuilder builder, Action<IServiceProvider, ResiliencePipelineBuilder> configure)
    {
        WriteStrategies.Add(configure);
        return builder.AddCallback();
    }

    public static ICachingBuilder ConfigureTelemetry(this ICachingBuilder builder, bool enabled = true, Action<TelemetryOptions>? configureOptions = null)
    {
        _telemetryEnabled = enabled;
        _telemetryConfig = enabled ? configureOptions : null;
        return builder;
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        builder.RegisterOnCompleteCallback(builder => builder.Services.TryAddSingleton<IResiliencePipelineHolder>(sp => sp.BuildResiliencePipelineHolder()));
        return builder;
    }

    private static ResiliencePipelineHolder BuildResiliencePipelineHolder(this IServiceProvider serviceProvider)
    {
        if (ReadStrategies.Count == 0 && WriteStrategies.Count == 0)
        {
            return ResiliencePipelineHolder.Empty;
        }

        var readBuilder = new ResiliencePipelineBuilder();
        ReadStrategies.ForEach(a => a(serviceProvider, readBuilder));
        TelemetryOptions? telemetryOptions = null;
        if (_telemetryEnabled)
        {
            telemetryOptions = new TelemetryOptions();
            _telemetryConfig?.Invoke(telemetryOptions);
            telemetryOptions.LoggerFactory ??= serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            readBuilder.ConfigureTelemetry(telemetryOptions);
        }
        if (WriteStrategies.Count == 0)
        {
            readBuilder.Name = "Caching";
            return new ResiliencePipelineHolder(new ResiliencePipelineWrapper(readBuilder.Build()));
        }

        var writerBuilder = new ResiliencePipelineBuilder();
        WriteStrategies.ForEach(a => a(serviceProvider, writerBuilder));
        if(_telemetryEnabled)
        {
            writerBuilder.ConfigureTelemetry(telemetryOptions!);
        }
        readBuilder.Name = "Caching.Read";
        writerBuilder.Name = "Caching.Write";

        return new ResiliencePipelineHolder(new ResiliencePipelineWrapper(readBuilder.Build()), new ResiliencePipelineWrapper(writerBuilder.Build()));
    }
}
