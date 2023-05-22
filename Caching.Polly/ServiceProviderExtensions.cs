namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class ServiceProviderExtensions
{
    public static IAsyncPolicy BuildCircuitBreakerPolicy(this IServiceProvider serviceProvider, ExecutePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ExecutePoliciesOptions>>();
        config ??= Options.Create(new ExecutePoliciesOptions());
        var redisCacheOptions = config.Value;
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("CircuitBreaker") ?? NullLogger.Instance;
        return Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: redisCacheOptions.ExceptionsAllowedBeforeBreaking,
                durationOfBreak: redisCacheOptions.DurationOfBreak,
                onBreak: (exception, breakDelay) => logger.LogWarning(exception, "CircuitBreaker for Redis operation: Breaking the circuit for {}!", redisCacheOptions.DurationOfBreak),
                onReset: () => logger.LogWarning("CircuitBreaker for Redis operation: Circuit closed, requests flow normally."),
                onHalfOpen: () => logger.LogWarning("CircuitBreaker for Redis operation: Circuit in test mode, one request will be allowed.")
            );
    }

    public static IAsyncPolicy? BuildTimeoutPolicy(this IServiceProvider serviceProvider, ExecutePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ExecutePoliciesOptions>>();
        config ??= Options.Create(new ExecutePoliciesOptions());
        var redisCacheOptions = config.Value;
        return redisCacheOptions.RequestTimeout.HasValue
            ? Policy.TimeoutAsync(redisCacheOptions.RequestTimeout.Value, TimeoutStrategy.Pessimistic)
            : default;
    }

    public static IAsyncPolicy? BuildRetryPolicy(this IServiceProvider serviceProvider, ExecutePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ExecutePoliciesOptions>>();
        config ??= Options.Create(new ExecutePoliciesOptions());
        var redisCacheOptions = config.Value;

        return redisCacheOptions.RetryCount.GetValueOrDefault() > 1
            ? Policy.Handle<Exception>().WaitAndRetryAsync(redisCacheOptions.RetryCount!.Value, x => TimeSpan.FromMilliseconds(x * 100))
            : default;
    }
}
