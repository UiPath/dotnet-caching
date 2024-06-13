using System;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace UiPath.Platform.Caching.Polly;

[ExcludeFromCodeCoverage]
public static class ServiceProviderExtensions
{
    public static void AddCircuitBreaker(this IServiceProvider serviceProvider, ResiliencePipelineBuilder builder, ResiliencePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ResiliencePoliciesOptions>>();
        config ??= Options.Create(new ResiliencePoliciesOptions());
        var resilienceOptions = config.Value;
        
        if (!resilienceOptions.Enabled)
        {
            return;
        }

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("CircuitBreakerStrategy") ?? NullLogger.Instance;
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            BreakDuration = resilienceOptions.DurationOfBreak,
            MinimumThroughput = resilienceOptions.ExceptionsAllowedBeforeBreaking,
            OnHalfOpened = args =>
            {
                logger.LogWarning("CircuitBreaker OnHalfOpened. Operation key {OperationKey}", args.Context.OperationKey);
                return default;
            },
            OnClosed = args =>
            {
                logger.LogWarning("CircuitBreaker OnClosed. Operation key {OperationKey}", args.Context.OperationKey);
                return default;
            },
            OnOpened = args =>
            {
                logger.LogWarning("CircuitBreaker OnOpened. Operation key {OperationKey}. Breaking the circuit for {DurationOfBreak}!", args.Context.OperationKey, resilienceOptions.DurationOfBreak);
                return default;
            }
        });
    }

    public static void AddTimeoutPolicy(this IServiceProvider serviceProvider, ResiliencePipelineBuilder builder, ResiliencePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ResiliencePoliciesOptions>>();
        config ??= Options.Create(new ResiliencePoliciesOptions());
        var resilienceOptions = config.Value;
        if (!resilienceOptions.Enabled || !resilienceOptions.RequestTimeout.HasValue)
        {
            return;
        }

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("TimeoutStrategy") ?? NullLogger.Instance;
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = resilienceOptions.RequestTimeout.Value,
            OnTimeout = args =>
            {
                logger.LogWarning("Execution timed out after {TotalMilliseconds} ms. Operation key {OperationKey}", args.Timeout.TotalMilliseconds, args.Context.OperationKey);
                return default;
            }
        });
    }

    public static void AddRetryPolicy(this IServiceProvider serviceProvider, ResiliencePipelineBuilder builder, ResiliencePoliciesOptions? options = null)
    {
        var config = options != null ? Options.Create(options) : serviceProvider.GetService<IOptions<ResiliencePoliciesOptions>>();
        config ??= Options.Create(new ResiliencePoliciesOptions());
        var executeOptions = config.Value;

        if (!executeOptions.Enabled || executeOptions.RetryCount.GetValueOrDefault() <= 1)
        {
            return;
        }

        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("RetryStrategy") ?? NullLogger.Instance;

        builder.AddRetry(new RetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            MaxRetryAttempts = executeOptions.RetryCount!.Value,
            Delay = TimeSpan.FromSeconds(1),
            BackoffType = DelayBackoffType.Constant,
            DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(args.AttemptNumber * 100)),
            OnRetry = args =>
            {
                logger.LogWarning("OnRetry, Attempt: {AttemptNumber}. Operation key {OperationKey}", args.AttemptNumber, args.Context.OperationKey);
                return default;
            }
        });
    }
}
