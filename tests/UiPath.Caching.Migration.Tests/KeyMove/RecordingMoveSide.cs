using StackExchange.Redis;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

internal sealed class RecordingMoveSide(IMoveSide<Item> inner, Func<KeyMoveSnap, KeyMoveSnap>? onSnapshot = null) : IMoveSide<Item>
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly List<string> _calls = [];

    public IKeyMoveCodec<Item> Codec
    {
        get
        {
            Record("Codec");
            return inner.Codec;
        }
    }

    public bool IsAvailable
    {
        get
        {
            Record("IsAvailable");
            return inner.IsAvailable;
        }
    }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public string Key(string logical)
    {
        Record("Key");
        return inner.Key(logical);
    }

    public bool Maps(string logical)
    {
        Record("Maps");
        return inner.Maps(logical);
    }

    public async ValueTask<KeyMoveSnap> SnapshotAsync(string logical, CancellationToken cancellationToken)
    {
        Record("Snapshot");
        var snap = await inner.SnapshotAsync(logical, cancellationToken);
        return onSnapshot is null ? snap : onSnapshot(snap);
    }

    public ValueTask<bool?> ExistsAsync(string logical, CancellationToken cancellationToken)
    {
        Record("Exists");
        return inner.ExistsAsync(logical, cancellationToken);
    }

    public Task<RedisValue> ControlIdAsync(string logical)
    {
        Record("ControlId");
        return inner.ControlIdAsync(logical);
    }

    public Task<long> SetAsync(string logical, string id, RedisValue bytes, long px, bool onlyIfAbsent)
    {
        Record("Set");
        return inner.SetAsync(logical, id, bytes, px, onlyIfAbsent);
    }

    public Task<long> DeleteAsync(string logical, string id)
    {
        Record("Delete");
        return inner.DeleteAsync(logical, id);
    }

    public Task<long> CompareDeleteAsync(string logical, string id, RedisValue expected, Func<bool>? owned = null)
    {
        Record("CompareDelete");
        return inner.CompareDeleteAsync(logical, id, expected);
    }

    public Task<bool> CreateAsync(string logical, RedisValue bytes, long px)
    {
        Record("Create");
        return inner.CreateAsync(logical, bytes, px);
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }
}
