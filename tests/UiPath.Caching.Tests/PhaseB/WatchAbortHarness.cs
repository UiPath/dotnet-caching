using StackExchange.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// A database whose transactions are made to abort with every predicate still holding: before the first EXEC attempts, the key is WATCHed on the
// connection the transaction uses, and another connection then writes it. The transaction's own WATCH comes later and keeps the first one's mark,
// so the server aborts EXEC although the conditions it reads are still true.
internal sealed class WatchAbortHarness : IAsyncDisposable
{
    private readonly IDatabase _real;
    private readonly ConnectionMultiplexer _otherMux;
    private readonly RedisKey _watched;
    private readonly RedisValue _field;
    private readonly int _skip;
    private readonly bool _absent;
    private readonly long _toForce;
    private int _executions;
    private int _aborted;

    private WatchAbortHarness(IDatabase real, ConnectionMultiplexer otherMux, RedisKey watched, RedisValue field, long toForce, int skip, bool absent)
    {
        _real = real;
        _otherMux = otherMux;
        _watched = watched;
        _field = field;
        _toForce = toForce;
        _skip = skip;
        _absent = absent;
        Database = InterceptingProxy.Wrap(real, InterceptDatabase);
    }

    public IDatabase Database { get; }

    public int Executions => Volatile.Read(ref _executions);

    // Transactions the server answered with an aborted EXEC, forced or not.
    public int Aborted => Volatile.Read(ref _aborted);

    // The first <paramref name="skip"/> transactions run untouched, the next <paramref name="toForce"/> are aborted.
    // The watched key is the environment's control record unless <paramref name="watched"/> names another; for a key that must stay absent,
    // <paramref name="absent"/> deletes it again after the write, which still marks it as touched.
    public static async Task<WatchAbortHarness> CreateAsync(MoveTestEnvironment env, string connectionString, long toForce, string field = "dur", int skip = 0, RedisKey? watched = null, bool absent = false) =>
        new(env.Db, await ConnectionMultiplexer.ConnectAsync(connectionString), watched ?? env.Ctl, field, toForce, skip, absent);

    public async ValueTask DisposeAsync() => await _otherMux.DisposeAsync();

    private object? InterceptDatabase(System.Reflection.MethodInfo method, object?[] args)
    {
        if (method.Name != nameof(IDatabase.CreateTransaction))
        {
            return InterceptingProxy.Proceed;
        }

        var inner = _real.CreateTransaction(args.Length > 0 ? args[0] : null);
        return InterceptingProxy.Wrap(inner, (m, a) => InterceptTransaction(inner, m, a));
    }

    private object? InterceptTransaction(ITransaction inner, System.Reflection.MethodInfo method, object?[] args) =>
        method.Name == nameof(ITransaction.ExecuteAsync) ? ExecuteAsync(inner, (CommandFlags)args[0]!) : InterceptingProxy.Proceed;

    private async Task<bool> ExecuteAsync(ITransaction inner, CommandFlags flags)
    {
        var attempt = Interlocked.Increment(ref _executions);
        if (attempt > _skip && attempt - _skip <= _toForce)
        {
            await _real.ExecuteAsync("WATCH", _watched).ConfigureAwait(false);
            await _otherMux.GetDatabase().HashSetAsync(_watched, _field, "0").ConfigureAwait(false);
            if (_absent)
            {
                await _otherMux.GetDatabase().KeyDeleteAsync(_watched).ConfigureAwait(false);
            }
        }

        var committed = await inner.ExecuteAsync(flags).ConfigureAwait(false);
        if (!committed)
        {
            Interlocked.Increment(ref _aborted);
        }

        return committed;
    }
}
