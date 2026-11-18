namespace UiPath.Caching.Migration;

/// <summary>Advanced by every clear of one cache; local entries written under an older value are no longer served.</summary>
internal sealed class CacheEpoch
{
    private long _value;

    public long Value => Volatile.Read(ref _value);

    public void Advance() => Interlocked.Increment(ref _value);
}
