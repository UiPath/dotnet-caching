namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class ServiceProviderExtensions
{
    public static IAsyncPolicy? BuildCircuitBreakerPolicy(this IServiceProvider serviceProvider, ResiliencePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ResiliencePoliciesOptions>>();
        config ??= Options.Create(new ResiliencePoliciesOptions());
        var resilienceOptions = config.Value;
        
        if (!resilienceOptions.Enabled)
        {
            return default;
        }

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("CircuitBreaker") ?? NullLogger.Instance;
        return Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: resilienceOptions.ExceptionsAllowedBeforeBreaking,
                durationOfBreak: resilienceOptions.DurationOfBreak,
                onBreak: (exception, breakDelay) => logger.LogWarning(exception, "CircuitBreaker for Redis operation: Breaking the circuit for {}!", resilienceOptions.DurationOfBreak),
                onReset: () => logger.LogWarning("CircuitBreaker for Redis operation: Circuit closed, requests flow normally."),
                onHalfOpen: () => logger.LogWarning("CircuitBreaker for Redis operation: Circuit in test mode, one request will be allowed.")
            );
    }

    public static IAsyncPolicy? BuildTimeoutPolicy(this IServiceProvider serviceProvider, ResiliencePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ResiliencePoliciesOptions>>();
        config ??= Options.Create(new ResiliencePoliciesOptions());
        var resilienceOptions = config.Value;
        return resilienceOptions.Enabled &&  resilienceOptions.RequestTimeout.HasValue
            ? Policy.TimeoutAsync(resilienceOptions.RequestTimeout.Value, TimeoutStrategy.Pessimistic)
            : default;
    }

    public static IAsyncPolicy? BuildRetryPolicy(this IServiceProvider serviceProvider, ResiliencePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ResiliencePoliciesOptions>>();
        config ??= Options.Create(new ResiliencePoliciesOptions());
        var executeOptions = config.Value;

        return executeOptions.Enabled && executeOptions.RetryCount.GetValueOrDefault() > 1
            ? Policy.Handle<Exception>().WaitAndRetryAsync(executeOptions.RetryCount!.Value, x => TimeSpan.FromMilliseconds(x * 100))
            : default;
    }
}
