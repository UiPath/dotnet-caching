namespace UiPath.Caching.Tests;

/// <summary>
/// Turning a resolved duration into a deadline. <see cref="TimeSpan.MaxValue"/> is the configured
/// spelling of "no TTL" and exceeds the remaining <see cref="DateTime"/> range from any modern date,
/// so the conversion has to saturate onto <see cref="DateTimeOffset.MaxValue"/> — the sentinel the
/// providers read as "no TTL" — instead of throwing.
/// </summary>
public class TimeProviderExtensionsTests
{
    [Fact]
    public void An_unbounded_duration_lands_on_MaxValue_instead_of_overflowing() =>
        TimeProvider.System.ToDateTimeOffset(TimeSpan.MaxValue).Should().Be(DateTimeOffset.MaxValue);

    /// <summary>
    /// The clock contract is UTC, but a fake can report any offset. Add advances the wall-clock
    /// <see cref="DateTime"/>, so a positive offset leaves less room than the UTC instant suggests;
    /// normalizing before the headroom check keeps the conversion total whatever the fake does.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    [InlineData(0)]
    public void Saturates_near_the_boundary_whatever_the_offset(int offsetHours)
    {
        var now = new DateTimeOffset(DateTime.MaxValue.AddHours(-10), TimeSpan.Zero)
            .ToOffset(TimeSpan.FromHours(offsetHours));
        var sut = new FakeTimeProvider(now);

        var act = () => sut.ToDateTimeOffset(TimeSpan.FromHours(9));

        act.Should().NotThrow().Which.Should().BeOnOrBefore(DateTimeOffset.MaxValue);
    }

    [Fact]
    public void Adds_normally_when_the_result_fits()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(5));
        var sut = new FakeTimeProvider(now);

        sut.ToDateTimeOffset(TimeSpan.FromHours(2)).Should().Be(now.AddHours(2));
    }

    /// <summary>
    /// The clock fills no default: a key with no TTL in storage reads as "no TTL" rather than a
    /// fabricated deadline, on both the duration and the deadline overload.
    /// </summary>
    [Fact]
    public void Null_reads_as_unbounded()
    {
        var sut = TimeProvider.System;

        sut.ToDateTimeOffset(default(TimeSpan?)).Should().Be(DateTimeOffset.MaxValue);
        sut.ToDateTimeOffset(default(DateTimeOffset?)).Should().Be(DateTimeOffset.MaxValue);
    }
}
