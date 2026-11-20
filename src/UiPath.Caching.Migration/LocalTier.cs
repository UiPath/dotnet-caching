using Microsoft.Extensions.Caching.Memory;

namespace UiPath.Caching.Migration;

/// <summary>One memory cache for every cache of the node, with no size limit (an entry lives at most its cache's cap) and expiry decided on the injected clock; marks are striped, so keys sharing a stripe can reject each other's fills, which costs a refill and never a stale read.</summary>
internal sealed class LocalTier : IDisposable
{
    private const int Stripes = 1024;

    private readonly TimeProvider _clock;
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly long[] _marks = new long[Stripes];
    private readonly object[] _locks = Enumerable.Range(0, Stripes).Select(_ => new object()).ToArray();
    private readonly HashSet<LocalKey>[] _pending = Enumerable.Range(0, Stripes).Select(_ => new HashSet<LocalKey>()).ToArray();

    public LocalTier(TimeProvider clock) => _clock = clock;

    public FillMark Capture(LocalKey key, CacheEpoch epoch)
    {
        var stripe = StripeOf(key);
        lock (_locks[stripe])
        {
            return new FillMark(_marks[stripe], epoch.Value);
        }
    }

    public bool TryGet(LocalKey key, CacheEpoch epoch, out object? value)
    {
        value = null;
        if (!_memory.TryGetValue(key, out LocalEntry? entry) || entry is null)
        {
            return false;
        }

        if (entry.Epoch != epoch.Value || entry.Deadline <= _clock.GetUtcNow())
        {
            _memory.Remove(key);
            return false;
        }

        value = entry.Value;
        return true;
    }

    public bool TryFill(LocalKey key, FillMark mark, CacheEpoch epoch, object value, DateTimeOffset deadline)
    {
        var lifetime = deadline - _clock.GetUtcNow();
        if (lifetime <= TimeSpan.Zero)
        {
            return false;
        }

        var stripe = StripeOf(key);
        lock (_locks[stripe])
        {
            if (_marks[stripe] != mark.Stripe || epoch.Value != mark.Epoch || _pending[stripe].Contains(key))
            {
                return false;
            }

            _memory.Set(key, new LocalEntry(value, deadline, mark.Epoch), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime });
            return true;
        }
    }

    public void Evict(LocalKey key)
    {
        var stripe = StripeOf(key);
        lock (_locks[stripe])
        {
            _marks[stripe]++;
            _memory.Remove(key);
        }
    }

    /// <summary>Drops every entry and advances every mark, so a fill whose read started before is rejected; keys held pending stay pending.</summary>
    public void Clear()
    {
        for (var stripe = 0; stripe < Stripes; stripe++)
        {
            lock (_locks[stripe])
            {
                _marks[stripe]++;
            }
        }

        _memory.Clear();
    }

    public void BeginPending(LocalKey key)
    {
        var stripe = StripeOf(key);
        lock (_locks[stripe])
        {
            _pending[stripe].Add(key);
        }
    }

    public void EndPending(LocalKey key)
    {
        var stripe = StripeOf(key);
        lock (_locks[stripe])
        {
            _pending[stripe].Remove(key);
        }
    }

    public void Dispose() => _memory.Dispose();

    private static int StripeOf(LocalKey key) => (int)((uint)key.Hash() % Stripes);
}
