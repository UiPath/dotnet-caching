using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private const string DefaultSectionName = "ResiliencePolicies";

    private static int _callBackRegistered = 0;

    private static readonly List<Func<IServiceProvider, IAsyncPolicy>> ReadPolicies = new();

    private static readonly List<Func<IServiceProvider, IAsyncPolicy>> WritePolicies = new();

    public static ICachingBuilder AddResiliencePolicies(this ICachingBuilder builder) =>
        builder.AddResiliencePolicies(DefaultSectionName);

    public static ICachingBuilder AddResiliencePolicies(this ICachingBuilder builder, string sectionName) =>
        builder.AddResiliencePolicies(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddResiliencePolicies(this ICachingBuilder builder, Action<ExecutePoliciesOptions> configureOptions)
    {
        ExecutePoliciesOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddReadPolicy(this ICachingBuilder builder, IAsyncPolicy policy)
    {
        ReadPolicies.Add(sp => policy);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddReadPolicy(this ICachingBuilder builder, Func<IServiceProvider, IAsyncPolicy> factory)
    {
        ReadPolicies.Add(factory);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddWritePolicy(this ICachingBuilder builder, IAsyncPolicy policy)
    {
        WritePolicies.Add(sp => policy);
        return builder.AddCallback();
    }

    public static ICachingBuilder AddWritePolicy(this ICachingBuilder builder, Func<IServiceProvider, IAsyncPolicy> factory)
    {
        WritePolicies.Add(factory);
        return builder.AddCallback();
    }

    private static ICachingBuilder AddCallback(this ICachingBuilder builder)
    {
        if (Interlocked.Exchange(ref _callBackRegistered, 1) == 0)
        {
            builder.RegisterOnCompleteCallback(builder => builder.Services.TryAddSingleton<IPolicyHolder>(sp => sp.BuildPolicyHolder()));
        }

        return builder;
    }

    private static PollyHolder BuildPolicyHolder(this IServiceProvider sp)
    {
        IAsyncPolicy[]? readPolicies = null;
        IAsyncPolicy[]? writePolicies = null;

        if (ReadPolicies.Any())
        {
            readPolicies = ReadPolicies.Select(factory => factory(sp)).ToArray();
            writePolicies = WritePolicies.Select(factory => factory(sp)).ToArray();
            if (!writePolicies.Any())
            {
                writePolicies = readPolicies;
            }
        }
        else
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
            readPolicies = policies.ToArray();
            writePolicies = WritePolicies.Select(factory => factory(sp)).ToArray();
            if (!writePolicies.Any())
            {
                writePolicies = readPolicies;
            }
        }

        return new PollyHolder(readPolicies, writePolicies);
    }
}
