namespace UiPath.Platform.Caching.Broadcast.Redis;

internal class RedisChannelPublisher : IChannelPublisher
{
    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly IEventFormatterProxy _formatter;
    private readonly ILogger<RedisChannelPublisher> _logger;

    public RedisChannelPublisher(Func<IDatabase> databaseAccessor, IEventFormatterProxy formatter, ILogger<RedisChannelPublisher> logger)
    {
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _formatter = formatter;
        _logger = logger;
    }

    private IDatabase Database => _lazyDatabase.Value;

    public async Task PublishAsync(Channel channel, IClearCacheEvent clearCacheEvent, CancellationToken token = default)
    {
        RedisChannel redisChannel = (string)channel;
        token.ThrowIfCancellationRequested();

        try
        {
            var message = _formatter.EncodeAsString(clearCacheEvent);
            _logger.LogTrace("Publishing to channel {} event {}", channel, clearCacheEvent.Id);
            await Database.PublishAsync(redisChannel, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when publishing to channel {}", channel);
        }
    }
}
