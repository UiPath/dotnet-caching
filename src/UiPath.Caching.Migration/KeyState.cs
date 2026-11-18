namespace UiPath.Caching.Migration;

internal sealed class KeyState : IDisposable
{
    private long _generation;

    public SemaphoreSlim Lock { get; } = new(1, 1);

    public int Users { get; set; }

    public long Generation => Volatile.Read(ref _generation);

    public void Advance() => Interlocked.Increment(ref _generation);

    public void Dispose() => Lock.Dispose();
}
