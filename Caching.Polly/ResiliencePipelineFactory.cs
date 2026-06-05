using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Retry;
using Polly.Telemetry;

namespace UiPath.Platform.Caching.Polly;

public class ResiliencePipelineFactory(
    ILoggerFactory loggerFactory,
    TelemetryOptions? telemetryOptions,
    IOptionsMonitor<ResiliencePoliciesOptions> optionsAccessor)
    : IResiliencePipelineFactory
{
    protected ResiliencePoliciesOptions ResilienceOptions => optionsAccessor.CurrentValue;

    protected ILoggerFactory LoggerFactory => loggerFactory;

    protected TelemetryOptions? TelemetryOptions => telemetryOptions;

    public virtual ResiliencePipeline<TResult> Create<TResult>(string? scope, TResult defaultValue) =>
        GetBuilder(LoggerFactory.CreateLogger<ResiliencePipeline<TResult>>(), optionsAccessor.Get(scope ?? Options.DefaultName), defaultValue).Build();

    protected virtual ResiliencePipelineBuilder<TResult> GetBuilder<TResult>(ILogger logger, ResiliencePoliciesOptions resilienceOptions, TResult defaultValue)
    {
        var builder = new ResiliencePipelineBuilder<TResult>();
        if (!resilienceOptions.Enabled)
        {
            return builder;
        }

        if (!resilienceOptions.RethrowCircuitBreakerExceptions)
        {
            builder.AddFallback(new FallbackStrategyOptions<TResult>
            {
                
                ShouldHandle = new PredicateBuilder<TResult>().Handle<BrokenCircuitException>(),
                FallbackAction = args =>
                {
                    return Outcome.FromResultAsValueTask(defaultValue);
                },
                OnFallback = args =>
                {
                    logger.LogWarning("OnFallback. Operation key {OperationKey}", args.Context.OperationKey);
                    return default;
                }
            });
        }

        if (resilienceOptions.DurationOfBreak > TimeSpan.Zero && resilienceOptions.ExceptionsAllowedBeforeBreaking > 1)
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<TResult>
            {
                ShouldHandle = new PredicateBuilder<TResult>().Handle<Exception>(),
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

        if (resilienceOptions.RetryCount.GetValueOrDefault() > 0)
        {
            builder.AddRetry(new RetryStrategyOptions<TResult>
            {
                ShouldHandle = new PredicateBuilder<TResult>().Handle<Exception>(),
                MaxRetryAttempts = resilienceOptions.RetryCount!.Value,
                BackoffType = DelayBackoffType.Constant,
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(args.AttemptNumber * 100)),
                OnRetry = args =>
                {
                    logger.LogWarning("OnRetry, Attempt: {AttemptNumber}. Operation key {OperationKey}", args.AttemptNumber, args.Context.OperationKey);
                    return default;
                }
            });
        }

        if (resilienceOptions.RequestTimeout.GetValueOrDefault() > TimeSpan.Zero)
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = resilienceOptions.RequestTimeout.GetValueOrDefault(),
                OnTimeout = args =>
                {
                    logger.LogWarning("Execution timed out after {TotalMilliseconds} ms. Operation key {OperationKey}", args.Timeout.TotalMilliseconds, args.Context.OperationKey);
                    return default;
                }
            });
        }


        if (resilienceOptions.TelemetryEnabled && TelemetryOptions != null)
        {
            builder.ConfigureTelemetry(TelemetryOptions);
        }

        return builder;
    }
}
