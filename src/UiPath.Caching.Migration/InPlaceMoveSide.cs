using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

internal sealed class InPlaceMoveSide<T> : IMoveSide<T>
    where T : class
{
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private readonly IKeyMovePlane _plane;
    private readonly string _group;
    private readonly Func<string, string> _key;
    private readonly Func<string, bool>? _mappable;
    private readonly TimeProvider _clock;

    public InPlaceMoveSide(IKeyMovePlane plane, string group, Func<string, string> key, IKeyMoveCodec<T> codec, Func<string, bool>? mappable, TimeProvider clock)
    {
        _plane = plane ?? throw new ArgumentNullException(nameof(plane));
        _group = group;
        _key = key ?? throw new ArgumentNullException(nameof(key));
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _mappable = mappable;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public IKeyMoveCodec<T> Codec { get; }

    public bool IsAvailable => _plane.Data is { } data && data.IsConnected();

    // The data view becomes available when the library starts.
    private KeyMoveData Data => _plane.Data ?? throw new InvalidOperationException("The key move data view is not available.");

    public string Key(string logical) => _key(logical);

    public bool Maps(string logical) => _mappable?.Invoke(logical) != false;

    public async ValueTask<KeyMoveSnap> SnapshotAsync(string logical, CancellationToken cancellationToken)
    {
        try
        {
            var data = Data;
            var read = await data.Db.StringGetWithExpiryAsync(Key(logical), Master).ConfigureAwait(false);
            if (read.Value.IsNull)
            {
                return KeyMoveSnap.Of(ReadOutcome.Absent);
            }

            var ttl = read.Expiry is { } left ? (long)Math.Ceiling(left.TotalMilliseconds) : MoveScripts.NoExpiry;
            return new KeyMoveSnap(ReadOutcome.Found, (byte[]?)read.Value ?? [], ttl, _clock.GetUtcNow());
        }
        catch (RedisServerException ex) when (ex.Message.Contains("WRONGTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return KeyMoveSnap.Of(ReadOutcome.Unreadable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return KeyMoveSnap.Of(ReadOutcome.Unavailable);
        }
    }

    public async ValueTask<bool?> ExistsAsync(string logical, CancellationToken cancellationToken)
    {
        try
        {
            return await Data.Db.KeyExistsAsync(Key(logical), Master).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public Task<RedisValue> ControlIdAsync(string logical)
    {
        var data = Data;
        return data.Db.HashGetAsync(ControlOf(data, Key(logical)), "id", Master);
    }

    public Task<long> SetAsync(string logical, string id, RedisValue bytes, long px, bool onlyIfAbsent)
    {
        var data = Data;
        var key = Key(logical);
        return data.Scripts.SourceSetAsync(key, ControlOf(data, key), id, bytes, px, onlyIfAbsent);
    }

    public Task<long> DeleteAsync(string logical, string id)
    {
        var data = Data;
        var key = Key(logical);
        return data.Scripts.SourceDeleteAsync(key, ControlOf(data, key), id);
    }

    public Task<long> CompareDeleteAsync(string logical, string id, RedisValue expected, Func<bool>? owned = null)
    {
        var data = Data;
        var key = Key(logical);
        return data.Scripts.CompareDeleteAsync(key, ControlOf(data, key), id, expected, owned);
    }

    public Task<bool> CreateAsync(string logical, RedisValue bytes, long px) =>
        Data.Db.StringSetAsync(Key(logical), bytes, px < 0 ? null : TimeSpan.FromMilliseconds(px), When.NotExists, Master);

    private RedisKey ControlOf(KeyMoveData data, string key) => data.Names.ControlFor(_group, key);
}
