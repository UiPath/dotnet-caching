using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.Redis;

namespace UiPath.Caching.KeyMove;

// Dedicated connections keep WAIT from blocking ordinary operations.
internal sealed class SideConnections : IAsyncDisposable
{
    private readonly IDatabase _src;
    private readonly IDatabase _dst;
    private readonly KeyMoveStateOptions _options;
    private readonly IAsyncDisposable? _owned;

    internal SideConnections(IDatabase src, IDatabase dst, KeyMoveStateOptions options, IAsyncDisposable? owned = null)
    {
        _src = src;
        _dst = dst;
        _options = options;
        _owned = owned;
    }

    public static async Task<SideConnections> OpenAsync(IRedisConnector src, IRedisConnector dst, KeyMoveStateOptions options, string sourcePrefix, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(options);
        var srcMux = await ConnectAsync(src, options, cancellationToken).ConfigureAwait(false);
        try
        {
            var dstMux = await ConnectAsync(dst, options, cancellationToken).ConfigureAwait(false);
            var srcDatabase = srcMux.GetDatabase(src.Database.Database);
            return new SideConnections(
                sourcePrefix.Length == 0 ? srcDatabase : srcDatabase.WithKeyPrefix(sourcePrefix),
                dstMux.GetDatabase(dst.Database.Database),
                options,
                new Owned(srcMux, dstMux));
        }
        catch
        {
            await srcMux.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public IDatabase Barrier(RecordSide side) => side == RecordSide.Src ? _src : _dst;

    public ClaimStore Claim(RecordSide side, MovePlacement placement, SideCaps caps)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(caps);
        return new ClaimStore(Barrier(side), placement.Claim(side).Key, caps.Scripts, caps.Version)
        {
            BarrierRetries = _options.BarrierRetries,
            BarrierTimeout = _options.BarrierWait,
        };
    }

    public GenerationReader Generations() => new(side => Barrier(side).Multiplexer);

    public void Attach(ConnectionChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        changes.Watch(_src.Multiplexer, RecordSide.Src);
        changes.Watch(_dst.Multiplexer, RecordSide.Dst);
    }

    public ValueTask DisposeAsync() => _owned?.DisposeAsync() ?? ValueTask.CompletedTask;

    internal static async Task<T> AwaitAsync<T>(Task<T> connect, CancellationToken cancellationToken)
        where T : class, IConnectionMultiplexer
    {
        try
        {
            return await connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = DisposeLateAsync(connect);
            throw;
        }
    }

    internal static async Task DisposeLateAsync<T>(Task<T> connect)
        where T : class, IConnectionMultiplexer
    {
        try
        {
            await (await connect.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connect failed or the dispose did: there is no connection left to own, and nobody waits for this task.
        }
    }

    // A connect can finish after cancellation, so dispose it when that happens.
    private static async Task<IConnectionMultiplexer> ConnectAsync(IRedisConnector connector, KeyMoveStateOptions options, CancellationToken cancellationToken)
    {
        var configuration = ConfigurationOptions.Parse(connector.Database.Multiplexer.Configuration);
        configuration.AllowAdmin = true;

        // WAIT blocks its connection for the barrier time; a command timeout below that would fail every barrier on a slow replica.
        var milliseconds = (int)(options.BarrierWait + TimeSpan.FromSeconds(5)).TotalMilliseconds;
        configuration.AsyncTimeout = Math.Max(configuration.AsyncTimeout, milliseconds);
        configuration.SyncTimeout = Math.Max(configuration.SyncTimeout, milliseconds);
        return await AwaitAsync(ConnectionMultiplexer.ConnectAsync(configuration), cancellationToken).ConfigureAwait(false);
    }

    private sealed class Owned(IConnectionMultiplexer first, IConnectionMultiplexer second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await first.DisposeAsync().ConfigureAwait(false);
            await second.DisposeAsync().ConfigureAwait(false);
        }
    }
}
