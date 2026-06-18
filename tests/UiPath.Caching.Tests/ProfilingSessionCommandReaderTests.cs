using StackExchange.Redis.Profiling;

namespace UiPath.Caching.Tests;

public class ProfilingSessionCommandReaderTests
{
    [Fact]
    public void NoSession()
    {
        var sut = new ProfilingSessionCommandReader();
        var ret = sut.Get(null);
        ret.Count.Should().Be(0);
        ret.SessionId.Should().BeNull();
        ret.Commands.Should().BeEmpty();
    }

    [Fact]
    public void WithSession()
    {
        var sut = new ProfilingSessionCommandReader();
        var ret = sut.Get(new ProfilingSession("test"));
        ret.Count.Should().Be(0);
        ret.SessionId.Should().Be("test");
        ret.Commands.Should().BeEmpty();
    }
}
