namespace UiPath.Caching.Broadcast.Redis;

internal sealed class RedisStreamsTopicOptionsValidator : IValidateOptions<RedisStreamsTopicOptions>
{
    // SemaphoreSlim.WaitAsync takes no longer timeout.
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(int.MaxValue);

    public ValidateOptionsResult Validate(string? name, RedisStreamsTopicOptions options)
    {
        if (options.PollInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{nameof(RedisStreamsTopicOptions.PollInterval)} must be greater than zero.");
        }
        if (options.PollInterval > MaxInterval)
        {
            return ValidateOptionsResult.Fail($"{nameof(RedisStreamsTopicOptions.PollInterval)} must be at most {MaxInterval}.");
        }
        if (options.NotifyMinInterval is { } spacing && (spacing < TimeSpan.Zero || spacing > RedisStreamsTopicOptions.MaxNotifyMinInterval))
        {
            return ValidateOptionsResult.Fail($"{nameof(RedisStreamsTopicOptions.NotifyMinInterval)} must be between zero and {RedisStreamsTopicOptions.MaxNotifyMinInterval}.");
        }
        if (options.NotifiedPollInterval is not { } notified)
        {
            return ValidateOptionsResult.Success;
        }
        if (notified <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{nameof(RedisStreamsTopicOptions.NotifiedPollInterval)} must be greater than zero.");
        }
        if (notified > MaxInterval)
        {
            return ValidateOptionsResult.Fail($"{nameof(RedisStreamsTopicOptions.NotifiedPollInterval)} must be at most {MaxInterval}.");
        }
        if (notified < options.PollInterval)
        {
            return ValidateOptionsResult.Fail($"{nameof(RedisStreamsTopicOptions.NotifiedPollInterval)} must be greater than or equal to {nameof(RedisStreamsTopicOptions.PollInterval)}.");
        }
        return ValidateOptionsResult.Success;
    }

    internal static void ThrowIfInvalid(RedisStreamsTopicOptions options)
    {
        var result = new RedisStreamsTopicOptionsValidator().Validate(name: null, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(nameof(RedisStreamsTopicOptions), typeof(RedisStreamsTopicOptions), result.Failures);
        }
    }
}
