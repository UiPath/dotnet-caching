using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Timeout;
using UiPath.Caching.Policies;
using UiPath.Caching.Polly;

namespace UiPath.Caching.Tests;
public class ResiliencePipelineWrapperTests(ITestContextAccessor testContextAccessor) : IAsyncLifetime
{
    private static readonly TimeSpan PipelineTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CallbackDuration = TimeSpan.FromSeconds(1);

    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();
    private IResiliencePipelineFactory _resiliencePipelineFactory = default!;
    private int _boolCallCount = 0;
    private int _intCallCount = 0;

    [Fact]
    public async Task IntPipelineIsCached_same_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        _intCallCount.Should().Be(1);
    }

    [Fact]
    public async Task IntPipeline_different_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 2, testContextAccessor.Current.CancellationToken);
        _intCallCount.Should().Be(2);
    }

    [Fact]
    public async Task BoolPipeline_different_default()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), true, testContextAccessor.Current.CancellationToken);
        _boolCallCount.Should().Be(2);
    }

    [Fact]
    public async Task AllCached()
    {
        var sut = _fixture.Create<ResiliencePipelineWrapper>();
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<int>(1), 1, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, testContextAccessor.Current.CancellationToken);
        await sut.ExecuteAsync(_ => new ValueTask<bool>(false), false, testContextAccessor.Current.CancellationToken);
        _boolCallCount.Should().Be(1);
        _intCallCount.Should().Be(1);
    }

    [Fact]
    public async Task The_read_pipeline_abandons_a_callback_that_ignores_the_token()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read);
        var elapsed = Stopwatch.StartNew();

        var act = async () => await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        elapsed.Elapsed.Should().BeLessThan(CallbackDuration);
    }

    [Theory]
    [InlineData(ResiliencePipelineNames.Write)]
    [InlineData("set-pop")]
    public async Task Other_pipelines_await_a_callback_that_ignores_the_token(string scope)
    {
        var sut = CreateTimedSut(scope);

        var result = await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue("the pipeline returned only once the callback had finished");
    }

    [Fact]
    public async Task A_callback_that_finishes_inside_the_timeout_is_unaffected()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read);

        var result = await sut.ExecuteAsync(_ => new ValueTask<bool>(true), false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task An_announced_window_widens_the_timeout()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, disruptionTimeout: TimeSpan.FromSeconds(3), disruptionInProgress: true, suggestedTimeout: TimeSpan.FromMilliseconds(120));

        var result = await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue("the call outlived the configured timeout but not the one the disruption allows");
    }

    [Fact]
    public async Task A_disruption_with_no_announced_window_does_not_widen_even_when_configured()
    {
        // The Azure probe route: in progress, with no announced window.
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, disruptionTimeout: TimeSpan.FromSeconds(3), disruptionInProgress: true, suggestedTimeout: null);

        var act = async () => await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task Outside_a_disruption_the_configured_timeout_still_applies()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, disruptionTimeout: TimeSpan.FromSeconds(3), disruptionInProgress: false);

        var act = async () => await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task A_disruption_is_ignored_when_no_disruption_timeout_is_configured()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, disruptionTimeout: null, disruptionInProgress: true);

        var act = async () => await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
    }

    [Fact]
    public async Task The_tier_suggestion_stands_in_for_an_unconfigured_disruption_timeout()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, disruptionInProgress: true, suggestedTimeout: TimeSpan.FromSeconds(3));

        var result = await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue("the tier is already relaxing its own timeouts by that much");
    }

    [Fact]
    public async Task A_configured_disruption_timeout_wins_over_the_suggestion()
    {
        var sut = CreateTimedSut(
            ResiliencePipelineNames.Read,
            disruptionTimeout: TimeSpan.FromSeconds(3),
            disruptionInProgress: true,
            suggestedTimeout: TimeSpan.FromMilliseconds(120));

        var result = await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task A_suggestion_below_the_normal_timeout_never_shortens_it()
    {
        // The callback outlives the suggestion but not the configured timeout.
        var sut = CreateTimedSut(
            ResiliencePipelineNames.Read,
            disruptionInProgress: true,
            suggestedTimeout: TimeSpan.FromMilliseconds(1),
            requestTimeout: TimeSpan.FromSeconds(3));

        var result = await sut.ExecuteAsync(IgnoresTheToken, false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue();
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
                _boolCallCount++;
                return new ResiliencePipelineBuilder<bool>().Build();
            });
        _resiliencePipelineFactory.Create(Arg.Any<string>(), Arg.Any<int>())
            .Returns(ctx =>
            {
                _intCallCount++;
                return new ResiliencePipelineBuilder<int>().Build();
            });
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Caller_cancellation_also_abandons_the_callback()
    {
        // A long timeout, so only the caller's token can release it.
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, requestTimeout: TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Cancelled once running, or Polly would refuse the call up front.
        var pending = sut.ExecuteAsync(
            async _ =>
            {
                entered.TrySetResult();
                await Task.Delay(CallbackDuration, CancellationToken.None).ConfigureAwait(false);
                return true;
            },
            false,
            caller.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var elapsed = Stopwatch.StartNew();
        await caller.CancelAsync();
        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>("the caller is released rather than waiting out the callback");
        elapsed.Elapsed.Should().BeLessThan(CallbackDuration, "it did not wait for the callback to finish");
    }

    [Fact]
    public async Task Caller_cancellation_waits_for_the_callback_on_the_write_pipeline()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Write, requestTimeout: TimeSpan.FromSeconds(30));
        using var caller = new CancellationTokenSource();

        var pending = sut.ExecuteAsync(IgnoresTheToken, false, caller.Token).AsTask();
        await caller.CancelAsync();

        (await pending).Should().BeTrue("the callback ran to its own completion");
    }

    [Fact]
    public async Task A_timed_out_read_is_not_retried()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, retryCount: 1);
        var attempts = 0;

        var act = async () => await sut.ExecuteAsync(
            token =>
            {
                Interlocked.Increment(ref attempts);
                return IgnoresTheToken(token);
            },
            false,
            testContextAccessor.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutRejectedException>();
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_open_the_circuit()
    {
        var sut = CreateTimedSut(ResiliencePipelineNames.Read, requestTimeout: TimeSpan.FromSeconds(30), exceptionsAllowedBeforeBreaking: 2);
        for (var i = 0; i < 5; i++)
        {
            using var caller = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = sut.ExecuteAsync(
                async _ =>
                {
                    entered.TrySetResult();
                    await Task.Delay(CallbackDuration, CancellationToken.None).ConfigureAwait(false);
                    return true;
                },
                false,
                caller.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await caller.CancelAsync();
            var act = async () => await pending;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        var result = await sut.ExecuteAsync(_ => new ValueTask<bool>(true), false, testContextAccessor.Current.CancellationToken);

        result.Should().BeTrue("an open circuit would have returned the fallback default instead");
    }

    private static ValueTask<bool> IgnoresTheToken(CancellationToken token) => RunIgnoringCancellation();

    private static async ValueTask<bool> RunIgnoringCancellation()
    {
        await Task.Delay(CallbackDuration, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static ResiliencePipelineWrapper CreateTimedSut(string scope, TimeSpan? disruptionTimeout = null, bool disruptionInProgress = false, TimeSpan? suggestedTimeout = null, TimeSpan? requestTimeout = null, int retryCount = 0, int exceptionsAllowedBeforeBreaking = 500)
    {
        var options = new ResiliencePoliciesOptions
        {
            RequestTimeout = requestTimeout ?? PipelineTimeout,
            RetryCount = retryCount,
            ExceptionsAllowedBeforeBreaking = exceptionsAllowedBeforeBreaking,
            TelemetryEnabled = false,
            DisruptionRequestTimeout = disruptionTimeout,
        };
        var monitor = Substitute.For<IOptionsMonitor<ResiliencePoliciesOptions>>();
        monitor.CurrentValue.Returns(_ => options);
        monitor.Get(Arg.Any<string>()).Returns(_ => options);
        var state = Substitute.For<IDisruptionState>();
        state.InProgress.Returns(disruptionInProgress);
        state.SuggestedTimeout.Returns(suggestedTimeout);

        return new(new ResiliencePipelineFactory(NullLoggerFactory.Instance, null, monitor, state), scope);
    }
}
