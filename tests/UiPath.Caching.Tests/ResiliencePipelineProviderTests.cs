using UiPath.Caching.Policies;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests;

public class ResiliencePipelineProviderTests
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private ResiliencePipelineProvider CreateSut(params string[] registered)
    {
        var registry = new ResiliencePipelineRegistry();
        foreach (var name in registered)
        {
            registry.Add(name);
        }
        return new(_fixture.Freeze<IResiliencePipelineFactory>(), registry);
    }

    [Theory]
    [InlineData(ResiliencePipelineNames.Read)]
    [InlineData(ResiliencePipelineNames.Write)]
    [InlineData("custom-scope")]
    public void Get_returns_pipeline_for_registered_name(string name)
    {
        var sut = CreateSut(name);

        sut.Get(name).Should().BeOfType<ResiliencePipelineWrapper>();
    }

    [Theory]
    [InlineData(ResiliencePipelineNames.Read)]
    [InlineData(ResiliencePipelineNames.Write)]
    [InlineData("custom-scope")]
    public void Get_caches_pipeline_per_name(string name)
    {
        var sut = CreateSut(name);

        sut.Get(name).Should().BeSameAs(sut.Get(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unregistered")]
    public void Get_returns_noop_for_null_empty_or_unregistered_name(string? name)
    {
        var sut = CreateSut(ResiliencePipelineNames.Read);

        sut.Get(name).Should().BeOfType<EmptyResiliencePipeline>();
    }
}
