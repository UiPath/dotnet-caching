namespace UiPath.Platform.Caching.Broadcast.Redis;

internal class RedisChannelPublisher<T> : IChannelPublisher<T> where T : class, IPubSubEvent
{
    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly IEventFormatterProxy<T> _formatter;
    private readonly ILogger<RedisChannelPublisher<T>> _logger;

    public RedisChannelPublisher(Func<IDatabase> databaseAccessor, IEventFormatterProxy<T> formatter, ILogger<RedisChannelPublisher<T>> logger)
    {
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _formatter = formatter;
        _logger = logger;
    }

    private IDatabase Database => _lazyDatabase.Value;

    public async Task PublishAsync(Channel channel, T pubSubEvent, CancellationToken token = default)
    {
        RedisChannel redisChannel = (string)channel;
        token.ThrowIfCancellationRequested();

        try
        {
            var message = _formatter.EncodeAsString(pubSubEvent);
            _logger.LogTrace("Publishing to channel {} event {}", channel, pubSubEvent.Id);
            await Database.PublishAsync(redisChannel, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when publishing to channel {}", channel);
        }
    }
}
