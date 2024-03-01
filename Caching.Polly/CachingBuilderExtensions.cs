using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class CachingBuilderExtensions
{
    private const string DefaultSectionName = "ResiliencePolicies";

    private static readonly List<Func<IServiceProvider, IAsyncPolicy?>> ReadPolicies = new();

    private static readonly List<Func<IServiceProvider, IAsyncPolicy?>> WritePolicies = new();

    public static ICachingBuilder AddResiliencePolicies(this ICachingBuilder builder) =>
        builder.AddResiliencePolicies(DefaultSectionName);

    public static ICachingBuilder AddResiliencePolicies(this ICachingBuilder builder, string sectionName) =>
        builder.AddResiliencePolicies(opt => builder.Configuration.GetSection(sectionName).Bind(opt));

    public static ICachingBuilder AddResiliencePolicies(this ICachingBuilder builder, Action<ResiliencePoliciesOptions> configureOptions)
    {
        ResiliencePoliciesOptions options = new();
        configureOptions.Invoke(options);
        builder.Services.Configure(configureOptions);
        ReadPolicies.Add(sp => sp.BuildCircuitBreakerPolicy(options));
        ReadPolicies.Add(sp => sp.BuildRetryPolicy(options));
        ReadPolicies.Add(sp => sp.BuildTimeoutPolicy(options));
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
        builder.RegisterOnCompleteCallback(builder => builder.Services.TryAddSingleton(sp => sp.BuildPolicyHolder()));
        return builder;
    }

    private static IPolicyHolder BuildPolicyHolder(this IServiceProvider sp)
    {
        var readPolicies = ReadPolicies.Select(factory => factory(sp)).OfType<IAsyncPolicy>().ToArray();
        var writePolicies = WritePolicies.Select(factory => factory(sp)).OfType<IAsyncPolicy>().ToArray();
        if(readPolicies.Length > 0 && writePolicies.Length == 0)
        {
            writePolicies = readPolicies;
        }

        if(readPolicies.Length == 0 && writePolicies.Length == 0)
        {
            return PolicyHolder.NoOp;
        }

        return new PollyHolder(readPolicies, writePolicies);
    }
}
