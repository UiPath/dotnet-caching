using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UiPath.Caching;

/// <summary>Keys with a fill in progress, each with a version that moves when the key changes: in shards, so a change takes only its own shard's lock, and none while no fill is in progress.</summary>
internal sealed class FillWatches
{
    private const int Shards = 64;

    private readonly Shard[] _shards = CreateShards();
    private int _watched;

    public bool Any => Volatile.Read(ref _watched) != 0;

    /// <summary>Starts a watch of the key, or joins the one running; the token is what <see cref="Intact"/> takes.</summary>
    public long Begin(string key)
    {
        var shard = ShardOf(key);
        lock (shard.Gate)
        {
            ref var watch = ref CollectionsMarshal.GetValueRefOrAddDefault(shard.Watches, key, out var exists);
            if (!exists)
            {
                watch.Version = ++shard.Sequence;
                Interlocked.Increment(ref _watched);
            }

            watch.Count++;
            return watch.Version + shard.Epoch;
        }
    }

    /// <summary>Another hold on the running watch of the key, which <see cref="End"/> gives back; none when the key is not watched.</summary>
    public void Retain(string key)
    {
        var shard = ShardOf(key);
        lock (shard.Gate)
        {
            ref var watch = ref CollectionsMarshal.GetValueRefOrNullRef(shard.Watches, key);
            if (!Unsafe.IsNullRef(ref watch))
            {
                watch.Count++;
            }
        }
    }

    public void End(string key)
    {
        var shard = ShardOf(key);
        lock (shard.Gate)
        {
            ref var watch = ref CollectionsMarshal.GetValueRefOrNullRef(shard.Watches, key);
            if (!Unsafe.IsNullRef(ref watch) && --watch.Count == 0)
            {
                shard.Watches.Remove(key);
                Interlocked.Decrement(ref _watched);
            }
        }
    }

    /// <summary>True while the key is watched and nothing has changed it since <paramref name="token"/> was taken.</summary>
    public bool Intact(string key, long token)
    {
        var shard = ShardOf(key);
        lock (shard.Gate)
        {
            ref var watch = ref CollectionsMarshal.GetValueRefOrNullRef(shard.Watches, key);
            return !Unsafe.IsNullRef(ref watch) && watch.Version + shard.Epoch == token;
        }
    }

    public void Changed(string key)
    {
        if (!Any)
        {
            return;
        }

        var shard = ShardOf(key);
        lock (shard.Gate)
        {
            ref var watch = ref CollectionsMarshal.GetValueRefOrNullRef(shard.Watches, key);
            if (!Unsafe.IsNullRef(ref watch))
            {
                watch.Version++;
            }
        }
    }

    /// <summary>Every watch of every key, running or later, is past the tokens taken so far.</summary>
    public void ChangedAll()
    {
        if (!Any)
        {
            return;
        }

        foreach (var shard in _shards)
        {
            lock (shard.Gate)
            {
                shard.Epoch++;
            }
        }
    }

    private static Shard[] CreateShards()
    {
        var shards = new Shard[Shards];
        for (var i = 0; i < shards.Length; i++)
        {
            shards[i] = new Shard();
        }

        return shards;
    }

    private Shard ShardOf(string key) => _shards[(uint)StringComparer.Ordinal.GetHashCode(key) % Shards];

    private struct Watch
    {
        public int Count;
        public long Version;
    }

    private sealed class Shard
    {
        public readonly Lock Gate = new();
        public readonly Dictionary<string, Watch> Watches = new(StringComparer.Ordinal);

        // Under Gate: the last version a watch began at, and the changes of every key at once.
        public long Sequence;
        public long Epoch;
    }
}
