using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UiPath.Caching.Config;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.OpenTelemetry;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private const string TelemetryEnabledKey = "TelemetryEnabled";

    public static ICachingBuilder AddOpenTelemetry(this ICachingBuilder builder) =>
        builder.AddOpenTelemetry(TelemetryEnabledKey);

    public static ICachingBuilder AddOpenTelemetry(this ICachingBuilder builder, string fieldName) =>
        builder.AddOpenTelemetry(builder.Configuration.GetValue<bool?>(fieldName).GetValueOrDefault(true));

    public static ICachingBuilder AddOpenTelemetry(this ICachingBuilder builder, bool enabled)
    {
        if (builder.Enabled && enabled)
        {
            builder.Services.TryAddSingleton<ICachingTelemetryProvider, CachingTelemetryProvider>();
        }
        else
        {
            builder.Services.TryAddSingleton<ICachingTelemetryProvider>(NullTelemetryProvider.Instance);
        }

        return builder;
    }
}
