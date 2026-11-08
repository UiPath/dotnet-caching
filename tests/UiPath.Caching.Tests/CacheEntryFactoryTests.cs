namespace UiPath.Caching.Tests;

public class CacheEntryFactoryTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void Works_as_expected()
    {
        var sut = new CacheEntryFactory();
        var value = _fixture.Create<string>();
        var expiration = _fixture.Create<DateTimeOffset>();
        var properties = _fixture.Create<IDictionary<string, string?>>();
        var original = sut.Create(value, expiration, properties);
        original.Value.Should().Be(value);
        original.Expiration.Should().Be(expiration);
        original.Metadata.Should().BeEquivalentTo(properties);
        var newExpiration = _fixture.Create<DateTimeOffset>();
        var newProperties = _fixture.Create<IDictionary<string, string?>>();
        var clone = original.NewEntry(newExpiration, newProperties);
        clone.Expiration.Should().Be(newExpiration);
        clone.Metadata.Should().BeEquivalentTo(newProperties);
    }

    [Fact]
    public void An_untracked_entry_ignores_a_read_mark()
    {
        var entry = (IReadTracked)new CacheEntryFactory().Create("v", DateTimeOffset.MaxValue);
        entry.Track(false);

        entry.MarkRead(5);

        entry.IsTracked.Should().BeFalse();
        entry.LastRead.Should().Be(IReadTracked.NeverRead);
    }

    [Fact]
    public void An_untracked_entry_that_is_tracked_again_takes_read_marks()
    {
        var entry = (IReadTracked)new CacheEntryFactory().Create("v", DateTimeOffset.MaxValue);
        entry.Track(false);

        entry.Track(true);
        entry.MarkRead(5);

        entry.IsTracked.Should().BeTrue();
        entry.LastRead.Should().Be(5);
    }

    [Fact]
    public void Tracking_an_entry_again_keeps_its_read_mark()
    {
        var entry = (IReadTracked)new CacheEntryFactory().Create("v", DateTimeOffset.MaxValue);
        entry.MarkRead(5);

        entry.Track(true);

        entry.LastRead.Should().Be(5);
    }

    [Fact]
    public void A_tracked_entry_takes_a_read_mark()
    {
        var entry = (IReadTracked)new CacheEntryFactory().Create("v", DateTimeOffset.MaxValue);

        entry.MarkRead(5);

        entry.IsTracked.Should().BeTrue();
        entry.LastRead.Should().Be(5);
    }
}
