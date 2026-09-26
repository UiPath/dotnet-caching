using UiPath.Caching.Policies;

namespace UiPath.Caching.Tests;

public class ResiliencePipelineStateOverloadTests
{
    [Fact]
    public async Task A_pipeline_that_implements_only_the_callback_overload_still_receives_the_state()
    {
        var pipeline = new CallbackOnlyPipeline();
        IResiliencePipeline sut = pipeline;

        var result = await sut.ExecuteAsync(static (state, _) => new ValueTask<string>(state), "state", string.Empty, TestContext.Current.CancellationToken);

        result.Should().Be("state");
        pipeline.Calls.Should().Be(1);
    }

    private sealed class CallbackOnlyPipeline : IResiliencePipeline
    {
        public int Calls { get; private set; }

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, TResult defaultValue, CancellationToken cancellationToken = default)
        {
            Calls++;
            return callback(cancellationToken);
        }
    }
}
