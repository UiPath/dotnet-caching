namespace UiPath.Caching.Migration;

/// <summary>How long a written entry lives: a duration from the write, an absolute deadline, or the cache default.</summary>
public readonly record struct CacheExpiry
{
    private CacheExpiry(TimeSpan? relative, DateTimeOffset? deadline)
    {
        Relative = relative;
        Deadline = deadline;
    }

    public static CacheExpiry Default => default;

    public TimeSpan? Relative { get; }

    public DateTimeOffset? Deadline { get; }

    public static CacheExpiry After(TimeSpan lifetime) => new(lifetime, null);

    public static CacheExpiry At(DateTimeOffset deadline) => new(null, deadline);
}
