using UiPath.Platform.Caching.Policies;

namespace UiPath.Platform.Caching.Tests;
public class ResiliencePipelineHolderTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();

    [Fact]
    public void Create_with_read_write_policy()
    {
        var read = _fixture.Create<IResiliencePipeline>();
        var write = _fixture.Create<IResiliencePipeline>();
        var sut = new ResiliencePipelineHolder(read, write);
        sut.Read.Should().Be(read);
        sut.Write.Should().Be(write);
    }

    [Fact]
    public void Create_with_read_policy()
    {
        var read = _fixture.Create<IResiliencePipeline>();
        IResiliencePipeline? write = null;
        var sut = new ResiliencePipelineHolder(read, write);
        sut.Read.Should().Be(read);
        sut.Write.Should().Be(read);
    }
}
