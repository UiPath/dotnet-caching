using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests;
public class PolicyHolderTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public void Create_with_read_write_policy()
    {
        var read = _fixture.Create<IPolicyExecutor>();
        var write = _fixture.Create<IPolicyExecutor>();
        var sut = new PolicyHolder(read, write);
        sut.Read.Should().Be(read);
        sut.Write.Should().Be(write);
    }

    [Fact]
    public void Create_with_read_policy()
    {
        var read = _fixture.Create<IPolicyExecutor>();
        IPolicyExecutor? write = null;
        var sut = new PolicyHolder(read, write);
        sut.Read.Should().Be(read);
        sut.Write.Should().Be(read);
    }
}
