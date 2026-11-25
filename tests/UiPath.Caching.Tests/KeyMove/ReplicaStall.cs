using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

internal sealed class ReplicaStall(IServer server) : IAsyncDisposable
{
    public static async Task<ReplicaStall> StartAsync(ConnectionMultiplexer replica, int seconds)
    {
        var server = replica.GetServer(replica.GetEndPoints()[0]);
        await server.ExecuteAsync("CLIENT", "PAUSE", Math.Max(seconds, 60) * 1000, "WRITE");
        return new ReplicaStall(server);
    }

    public async Task FinishedAsync()
    {
        await server.ExecuteAsync("CLIENT", "UNPAUSE");
        await server.PingAsync();
    }

    public ValueTask DisposeAsync() => new(FinishedAsync());
}
