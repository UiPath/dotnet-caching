using Polly;
using UiPath.Platform.Caching.Polly;

namespace UiPath.Platform.Caching.Tests;
public class ResiliencePipelineWrapperTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private IResiliencePipelineFactory _resiliencePipelineFactory = default!;
    private int boolCallCount = 0;
    private int intCallCount = 0;

    [Fact]
    public async Task IntPipelineIsCached_same_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, default);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, default);
        intCallCount.Should().Be(1);
    }

    [Fact]
    public async Task IntPipeline_different_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, default);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 2, default);
        intCallCount.Should().Be(2);
    }

    [Fact]
    public async Task BoolPipeline_different_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, default);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), true, default);
        boolCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AllCached()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, default);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, default);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, default);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, default);
        boolCallCount.Should().Be(1);
        intCallCount.Should().Be(1);
    }

    public Task DisposeAsync()
    {

        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _resiliencePipelineFactory = _fixture.Freeze<IResiliencePipelineFactory>();
        _resiliencePipelineFactory.Create(Arg.Any<string>(), Arg.Any<bool>())
            .Returns(ctx =>
            {
                boolCallCount++;
                return new ResiliencePipelineBuilder<bool>().Build();
            });
        _resiliencePipelineFactory.Create(Arg.Any<string>(), Arg.Any<int>())
            .Returns(ctx =>
            {
                intCallCount++;
                return new ResiliencePipelineBuilder<int>().Build();
            });
        return Task.CompletedTask;
    }
}
