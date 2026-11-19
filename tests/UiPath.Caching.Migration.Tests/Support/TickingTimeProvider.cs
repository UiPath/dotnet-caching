namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>A clock that moves on by a fixed step every time it is read.</summary>
internal sealed class TickingTimeProvider(DateTimeOffset start, TimeSpan step) : TimeProvider
{
    private long _reads;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(step.Ticks * (Interlocked.Increment(ref _reads) - 1));
}
