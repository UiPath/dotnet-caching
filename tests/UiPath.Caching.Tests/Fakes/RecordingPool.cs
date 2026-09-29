using System.Buffers;
using System.Collections.Concurrent;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>Stands in for the writer's pool while alive: rents from the shared pool and records every array that comes back.</summary>
internal sealed class RecordingPool : ArrayPool<byte>, IDisposable
{
    private readonly ArrayPool<byte> _previous = PooledJsonWriter.Pool;
    private readonly ConcurrentBag<byte[]> _returned = [];
    private readonly ConcurrentBag<(int Thread, byte[] Array)> _rented = [];

    public RecordingPool() => PooledJsonWriter.Pool = this;

    public IReadOnlyCollection<byte[]> Returned => _returned;

    /// <summary>What the calling thread rented; other tests rent through this pool concurrently, so a per-thread view is the deterministic one.</summary>
    public IReadOnlyCollection<byte[]> RentedByThisThread => [.. _rented.Where(r => r.Thread == Environment.CurrentManagedThreadId).Select(r => r.Array)];

    public override byte[] Rent(int minimumLength)
    {
        var array = Shared.Rent(minimumLength);
        _rented.Add((Environment.CurrentManagedThreadId, array));
        return array;
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        _returned.Add(array);
        Shared.Return(array, clearArray);
    }

    public void Dispose() => PooledJsonWriter.Pool = _previous;
}
