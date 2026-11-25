using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

internal static class RedisSignals
{
    public static async Task ReplaceConnectionAsync(IConnectionMultiplexer connection, IConnectionMultiplexer admin, IServer server)
    {
        var restored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRestored(object? sender, ConnectionFailedEventArgs args)
        {
            if (args.ConnectionType == ConnectionType.Interactive && Equals(args.EndPoint, server.EndPoint))
            {
                restored.TrySetResult();
            }
        }

        connection.ConnectionRestored += OnRestored;
        try
        {
            var id = (long)await server.ExecuteAsync("CLIENT", "ID");
            await admin.GetServer(server.EndPoint).ExecuteAsync("CLIENT", "KILL", "ID", id);
            await restored.Task.WaitAsync(TestContext.Current.CancellationToken);
            ((long)await server.ExecuteAsync("CLIENT", "ID")).Should().NotBe(id);
        }
        finally
        {
            connection.ConnectionRestored -= OnRestored;
        }
    }
    public static async Task<ExpirySubscription> ObserveExpiryAsync(IConnectionMultiplexer connection, RedisKey key)
    {
        var options = ConfigurationOptions.Parse(connection.Configuration);
        options.AllowAdmin = true;
        var admin = await ConnectionMultiplexer.ConnectAsync(options);
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var before = (await server.ConfigGetAsync("notify-keyspace-events")).Single().Value;
        await server.ConfigSetAsync("notify-keyspace-events", "Ex");
        var subscriber = admin.GetSubscriber();
        var channel = RedisChannel.Literal("__keyevent@0__:expired");
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<RedisChannel, RedisValue> handler = (_, value) =>
        {
            if (value == key.ToString())
            {
                expired.TrySetResult();
            }
        };
        await subscriber.SubscribeAsync(channel, handler);
        return new ExpirySubscription(admin, server, subscriber, channel, handler, before, expired.Task);
    }

    internal sealed class ExpirySubscription(ConnectionMultiplexer admin, IServer server, ISubscriber subscriber, RedisChannel channel, Action<RedisChannel, RedisValue> handler, string before, Task expired) : IAsyncDisposable
    {
        public Task Expired => expired;

        public async ValueTask DisposeAsync()
        {
            await subscriber.UnsubscribeAsync(channel, handler);
            await server.ConfigSetAsync("notify-keyspace-events", before);
            await admin.DisposeAsync();
        }
    }
}
