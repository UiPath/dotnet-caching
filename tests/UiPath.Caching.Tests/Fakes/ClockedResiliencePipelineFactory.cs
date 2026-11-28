using Microsoft.Extensions.Logging;
using Polly;
using Polly.Telemetry;
using UiPath.Caching.Policies;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests.Fakes;

internal sealed class ClockedResiliencePipelineFactory(
    ILoggerFactory loggerFactory,
    TelemetryOptions? telemetryOptions,
    IOptionsMonitor<ResiliencePoliciesOptions> options,
    TimeProvider clock,
    IDisruptionState? disruptionState = null)
    : ResiliencePipelineFactory(loggerFactory, telemetryOptions, options, disruptionState)
{
    protected override ResiliencePipelineBuilder<TResult> GetBuilder<TResult>(ILogger logger, ResiliencePoliciesOptions resilienceOptions, TResult defaultValue)
    {
        var builder = base.GetBuilder(logger, resilienceOptions, defaultValue);
        builder.TimeProvider = clock;
        return builder;
    }
}
