using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;
using UiPath.Platform.Caching.Config;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private static bool CallBackRegistered = false;

    private static readonly List<Func<IServiceProvider, IAsyncPolicy>> ReadPolicies = new();

    private static readonly List<Func<IServiceProvider, IAsyncPolicy>> WritePolicies = new();

    public static ICachingBuilder ConfigureExecutePolicies(this ICachingBuilder builder, Action<ExecutePoliciesOptions> configureOptions)
    {
        ExecutePoliciesOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddDefaultPolicy(this ICachingBuilder builder) =>
        builder.AddCallback();

    public static ICachingBuilder AddReadPolicy(this ICachingBuilder builder, Func<IServiceProvider, IAsyncPolicy> factory)
    {
        ReadPolicies.Add(factory);
        return builder.AddCallback();
    }
    public static ICachingBuilder AddWritePolicy(this ICachingBuilder builder, Func<IServiceProvider, IAsyncPolicy> factory)
    {
        WritePolicies.Add(factory);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddReadPolicy(this ICachingBuilder builder, IAsyncPolicy policy)
    {
        ReadPolicies.Add(sp => policy);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddWritePolicy(this ICachingBuilder builder, IAsyncPolicy policy)
    {
        WritePolicies.Add(sp => policy);
        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        if (!CallBackRegistered)
        {
            builder.RegisterOnCompleteCallback(builder =>
            {

                if (ReadPolicies.Any())
                {
                    builder.Services.TryAddSingleton<IPolicyHolder>(sp =>
                    {
                        var readPolicies = ReadPolicies.Select(factory => factory(sp)).ToArray();
                        var writePolicies = WritePolicies.Select(factory => factory(sp)).ToArray();
                        if (!writePolicies.Any())
                        {
                            writePolicies = readPolicies;
                        }

                        return new PollyHolder(readPolicies, writePolicies);
                    });
                }
                else
                {
                    builder.Services.TryAddSingleton<IPolicyHolder>(sp =>
                    {
                        var policies = new List<IAsyncPolicy> { sp.BuildCircuitBreakerPolicy() };
                        var p = sp.BuildRetryPolicy();
                        if (p != null)
                        {
                            policies.Add(p);
                        }

                        p = sp.BuildTimeoutPolicy();
                        if (p != null)
                        {
                            policies.Add(p);
                        }
                        var readPolicies = policies.ToArray();
                        var writePolicies = WritePolicies.Select(factory => factory(sp)).ToArray();
                        if (!writePolicies.Any())
                        {
                            writePolicies = readPolicies;
                        }
                        return new PollyHolder(readPolicies, writePolicies);
                    });
                }
            });
            CallBackRegistered = true;
        }

        return builder;
    }
}
