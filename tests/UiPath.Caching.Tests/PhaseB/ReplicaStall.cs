using StackExchange.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// Stops a replica from processing anything for a while, and says so only once it is inside the sleep: a PING that stays pending proves it.
internal sealed class ReplicaStall
{
    private readonly Task _sleep;

    private ReplicaStall(Task sleep) => _sleep = sleep;

    public static async Task<ReplicaStall> StartAsync(ConnectionMultiplexer replica, int seconds)
    {
        var sleep = Task.Run(() => replica.GetServer(replica.GetEndPoints()[0]).ExecuteAsync("DEBUG", "SLEEP", seconds));
        for (var i = 0; i < 60; i++)
        {
            var ping = replica.GetDatabase().PingAsync();
            if (await Task.WhenAny(ping, Task.Delay(150)) != ping)
            {
                return new ReplicaStall(sleep);
            }
        }

        throw new TimeoutException("The replica never stopped answering.");
    }

    // The sleep has ended and the replica has caught up with the primary again.
    public async Task FinishedAsync()
    {
        await _sleep;
        await Task.Delay(1200);
    }
}
