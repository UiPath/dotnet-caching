using System.Text;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal class RedisChannelPublisher : IChannelPublisher
{
    private readonly Lazy<IDatabase> _lazyDatabase;
    private readonly CloudEventFormatter _formatter;
    private readonly ILogger<RedisChannelPublisher> _logger;

    public RedisChannelPublisher(Func<IDatabase> databaseAccessor, CloudEventFormatter formatter, ILogger<RedisChannelPublisher> logger)
    {
        _lazyDatabase = new Lazy<IDatabase>(databaseAccessor);
        _formatter = formatter;
        _logger = logger;
    }

    private IDatabase Database => _lazyDatabase.Value;


    public async Task PublishAsync(Channel channel, CloudEvent cloudEvent, CancellationToken token = default)
    {
        RedisChannel redisChannel = (string)channel;
        token.ThrowIfCancellationRequested();

        try
        {
            var bytes = _formatter.EncodeStructuredModeMessage(cloudEvent, out _);
            var message = Encoding.UTF8.GetString(bytes.Span);
            _logger.LogTrace("Publishing to channel {} event {}", channel, cloudEvent.Id);
            await Database.PublishAsync(redisChannel, message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when publishing to channel {}", channel);
        }
    }
}
