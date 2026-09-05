using Microsoft.Extensions.Internal;

namespace UiPath.Caching.Tests;

/// <summary>A <see cref="TimeProvider"/> stopped at one instant.</summary>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// A <see cref="TimeProvider"/> over an <see cref="ISystemClock"/>, for the tests that drive time through a
/// substituted <see cref="ISystemClock"/> and hand the same clock to a <c>MemoryCache</c> they build directly.
/// </summary>
public sealed class SystemClockTimeProvider(ISystemClock clock) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => clock.UtcNow;
}
