using StackExchange.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Tells whether anything wrote a key: the key is WATCHed on this probe's own connection, and a later EXEC aborts when another connection wrote it,
// even with the value it already held. Unlike a server-wide command count, other tests writing other keys never disturb it.
internal sealed class WriteProbe : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _mux;

    private WriteProbe(ConnectionMultiplexer mux) => _mux = mux;

    public static async Task<WriteProbe> CreateAsync(string connectionString) => new(await ConnectionMultiplexer.ConnectAsync(connectionString));

    public async Task WatchAsync(RedisKey key) => await _mux.GetDatabase().ExecuteAsync("WATCH", key);

    public async Task<bool> TouchedAsync()
    {
        var transaction = _mux.GetDatabase().CreateTransaction();
        _ = transaction.PingAsync();
        return !await transaction.ExecuteAsync();
    }

    public async ValueTask DisposeAsync() => await _mux.DisposeAsync();
}
