using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class TopicReleaseTests : RouterTestBase
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_topic_release_token_is_cancelled_once_the_cleanup_bound_has_passed_on_the_router_clock()
    {
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (core, router) = Started((_, token) =>
        {
            seen.TrySetResult(token);
            token.Register(() => step.TrySetCanceled(token));
            return step.Task;
        });

        var release = core.ReleaseLibraryAsync();
        var token = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        token.CanBeCanceled.Should().BeTrue();
        token.IsCancellationRequested.Should().BeFalse();
        Clock.Advance(Bound - TimeSpan.FromSeconds(1));
        token.IsCancellationRequested.Should().BeFalse("the bound has not passed");
        Clock.Advance(TimeSpan.FromSeconds(1));
        token.IsCancellationRequested.Should().BeTrue("the bound has passed");
        await release.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await router.DisposeAsync();
    }

    [Fact]
    public async Task A_topic_release_that_outlasts_its_bound_is_cut_off_when_the_router_gives_up_on_it()
    {
        var bound = TimeSpan.FromMilliseconds(200);
        var clock = new SignalingTimeProvider(Clock);
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var step = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (core, router) = Started(
            (_, token) =>
            {
                seen.TrySetResult(token);
                return step.Task;
            },
            bound,
            clock);

        var release = core.ReleaseLibraryAsync();
        var token = await seen.Task.WaitAsync(Ct);
        // The first timer cancels the token. The second lets the router move on.
        await clock.WaitForTimerWhileRunningAsync(bound, release, Ct);
        await clock.WaitForTimerWhileRunningAsync(bound, release, Ct);
        release.IsCompleted.Should().BeFalse();
        token.IsCancellationRequested.Should().BeFalse();

        clock.Advance(bound);
        await release.WaitAsync(Ct);

        token.IsCancellationRequested.Should().BeTrue("a step that ignored its token is cut off, not left holding it");
        step.Task.IsCompleted.Should().BeFalse();
        core.UnfinishedCleanup.Should().Be(1);
        await router.DisposeAsync();
    }

    private (RouterCore Core, Router Router) Started(Func<TopicKey, CancellationToken, Task> releaseTopic, TimeSpan? cleanupBound = null, TimeProvider? clock = null)
    {
        var core = new RouterCore(clock ?? Clock, _ => true, NullLogger.Instance) { CleanupBound = cleanupBound ?? Bound };
        var cache = new RoutedCache<Item>(core, new RouterCacheRegistration<Item> { Name = "first", Group = "g", Source = new FakeBackend(Store, Clock), RedisKey = key => key });
        var router = new Router(core, [("first", typeof(Item), cache)]);
        var made = Parts(new FakeMigrationCache());
        var parts = new LibraryParts(made.Store, made.Connection, made.Topics, made.Events, made.Source, made.LibrarySettleWithin, ReleaseTopic: releaseTopic);
        core.UseLibrary(_ => Task.FromResult(parts));
        router.Start();
        router.Mode.Should().Be(RouterMode.Library);
        return (core, router);
    }
}
