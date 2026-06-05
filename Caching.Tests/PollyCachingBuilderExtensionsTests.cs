using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Polly.Telemetry;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Policies;
using UiPath.Platform.Caching.Polly;

namespace UiPath.Platform.Caching.Tests;

public class PollyCachingBuilderExtensionsTests
{
    [Fact]
    public void Multiple_AddResilienceStrategies_in_same_process_each_register_real_pipeline_provider()
    {
        using var providerA = BuildContainer();
        using var providerB = BuildContainer();

        providerA.GetRequiredService<IResiliencePipelineProvider>()
            .Get(ResiliencePipelineNames.Read).Should().BeOfType<ResiliencePipelineWrapper>();
        providerB.GetRequiredService<IResiliencePipelineProvider>()
            .Get(ResiliencePipelineNames.Read).Should().BeOfType<ResiliencePipelineWrapper>();
    }

    [Fact]
    public void Telemetry_settings_do_not_bleed_across_hosts()
    {
        using var providerA = BuildContainer(opt => opt.TelemetryEnabled = true);
        using var providerB = BuildContainer(opt => opt.TelemetryEnabled = false);

        var factoryA = (ResiliencePipelineFactory)providerA.GetRequiredService<IResiliencePipelineFactory>();
        var factoryB = (ResiliencePipelineFactory)providerB.GetRequiredService<IResiliencePipelineFactory>();

        GetTelemetryOptions(factoryA).Should().NotBeNull();
        GetTelemetryOptions(factoryB).Should().BeNull();
    }

    private static ServiceProvider BuildContainer(Action<ResiliencePoliciesOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCaching(builder => builder.AddResilienceStrategies(configure ?? (_ => { })));
        return services.BuildServiceProvider();
    }

    private static TelemetryOptions? GetTelemetryOptions(ResiliencePipelineFactory factory) =>
        (TelemetryOptions?)typeof(ResiliencePipelineFactory)
            .GetProperty("TelemetryOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(factory);
}
