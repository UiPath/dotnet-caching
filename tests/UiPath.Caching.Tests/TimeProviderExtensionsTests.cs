namespace UiPath.Caching.Tests;

/// <summary>The saturating conversion: <see cref="TimeSpan.MaxValue"/> lands on <see cref="DateTimeOffset.MaxValue"/> instead of overflowing.</summary>
public class TimeProviderExtensionsTests
{
    [Fact]
    public void An_unbounded_duration_lands_on_MaxValue_instead_of_overflowing() =>
        TimeProvider.System.ToDateTimeOffset(TimeSpan.MaxValue).Should().Be(DateTimeOffset.MaxValue);

    /// <summary>A fake may report any offset; normalizing to UTC before the headroom check keeps the conversion total.</summary>
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

    [Fact]
    public void Null_reads_as_unbounded()
    {
        var sut = TimeProvider.System;

        sut.ToDateTimeOffset(default(TimeSpan?)).Should().Be(DateTimeOffset.MaxValue);
        sut.ToDateTimeOffset(default(DateTimeOffset?)).Should().Be(DateTimeOffset.MaxValue);
    }
}
