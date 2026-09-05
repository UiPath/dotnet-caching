using Microsoft.Extensions.Internal;

namespace UiPath.Caching.Tests;

/// <summary>A <see cref="TimeProvider"/> stopped at one instant.</summary>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A <see cref="TimeProvider"/> over a substituted <see cref="ISystemClock"/>, so a test can hand the same clock to a <c>MemoryCache</c> it builds.</summary>
public sealed class SystemClockTimeProvider(ISystemClock clock) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => clock.UtcNow;
}
