using Polly;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests;
public class ResiliencePipelineWrapperTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private IResiliencePipelineFactory _resiliencePipelineFactory = default!;
    private int boolCallCount = 0;
    private int intCallCount = 0;

    [Fact]
    public async Task IntPipelineIsCached_same_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        intCallCount.Should().Be(1);
    }

    [Fact]
    public async Task IntPipeline_different_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 2, testContextAccessor.Current.CancellationToken);
        intCallCount.Should().Be(2);
    }

    [Fact]
    public async Task BoolPipeline_different_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), true, testContextAccessor.Current.CancellationToken);
        boolCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AllCached()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, testContextAccessor.Current.CancellationToken);
        boolCallCount.Should().Be(1);
        intCallCount.Should().Be(1);
    }

    public ValueTask DisposeAsync()
    {

        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
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
        return ValueTask.CompletedTask;
    }
}
