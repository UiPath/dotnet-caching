using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

public sealed class AdvertisementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_entry_round_trips_with_its_tokens_and_escaped_text()
    {
        var written = new RouterAdvertisement("urn:host-p1;x=y", "3.2.0+abc", ["byp", "pin=g1", "rc=g1:7", "g1@4:ab12:S3f"], Left: true);

        RouterAdvertisement.TryParse(written.Encode(), out var read).Should().BeTrue();

        read!.Instance.Should().Be(written.Instance);
        read.Build.Should().Be(written.Build);
        read.Tokens.Should().Equal(written.Tokens);
        read.Left.Should().BeTrue();
        read.Has(RouterAdvertisement.Bypass).Should().BeTrue();
        written.Encode().Should().NotContain("vr=", "the format negotiation must not read a router entry as a node that reads values");
    }

    [Fact]
    public void Readers_skip_unknown_fields_and_tokens()
    {
        RouterAdvertisement.TryParse("kind=router;inst=n1;future=1;mig=zz9+byp++q=1:2;left=0", out var read).Should().BeTrue();

        read!.Has("byp").Should().BeTrue();
        read.Tokens.Should().Equal("zz9", "byp", "q=1:2");
        read.Left.Should().BeFalse();
        read.Build.Should().BeEmpty();
    }

    [Theory]
    [InlineData("lib=3.2.0;inst=n1;vr=json")]
    [InlineData("kind=other;inst=n1;mig=byp")]
    [InlineData("kind=router;mig=byp")]
    [InlineData("")]
    public void Text_that_is_not_a_router_entry_does_not_parse(string text) =>
        RouterAdvertisement.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public async Task Bypass_toggles_the_token()
    {
        await using var node = TestNode.Create(new FakeBus(), new ManualTimeProvider(DateTimeOffset.UnixEpoch), [], "c");
        var advertiser = Advertiser(() => throw new RedisException("down"), node.Core);
        advertiser.Current().Has(RouterAdvertisement.Bypass).Should().BeFalse();

        await node.Router.EnterBypassAsync(Ct);
        advertiser.Current().Has(RouterAdvertisement.Bypass).Should().BeTrue();

        await node.Router.LeaveBypassAsync(Ct);
        advertiser.Current().Has(RouterAdvertisement.Bypass).Should().BeFalse();
    }

    [Fact]
    public async Task Redis_down_never_throws_and_the_loop_keeps_retrying()
    {
        var attempts = 0;
        var clock = new SignalingTimeProvider(DateTimeOffset.UnixEpoch);
        var core = new RouterCore(clock, _ => true, NullLogger.Instance);
        var advertiser = Advertiser(
            () =>
            {
                Interlocked.Increment(ref attempts);
                throw new RedisException("down");
            },
            core,
            interval: TimeSpan.FromMilliseconds(20));

        (await advertiser.WriteAsync(Ct)).Should().BeFalse();
        advertiser.Start();
        var interval = TimeSpan.FromMilliseconds(20);
        await clock.WaitForTimerAsync(interval, Ct);
        clock.Advance(interval);
        await clock.WaitForTimerAsync(interval, Ct);
        clock.Advance(interval);
        await clock.WaitForTimerAsync(interval, Ct);

        var stop = () => advertiser.StopAsync(TimeSpan.FromSeconds(1));
        await stop.Should().NotThrowAsync();
        Volatile.Read(ref attempts).Should().BeGreaterThanOrEqualTo(4, "the departing write is attempted too, and nothing threw");
    }

    [Fact]
    public async Task A_stalled_connection_never_holds_the_stop()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var core = new RouterCore(clock, _ => true, NullLogger.Instance);
        var advertiser = Advertiser(
            () =>
            {
                entered.TrySetResult();
                release.Wait(Ct);
                throw new RedisException("released");
            },
            core);
        advertiser.Start();
        try
        {
            await entered.Task.WaitAsync(Ct);
            var bound = TimeSpan.FromMilliseconds(200);
            var stopping = advertiser.StopAsync(bound);
            clock.Advance(bound);
            await stopping.WaitAsync(Ct);
            release.IsSet.Should().BeFalse("the stop ended on its bound while the write was still stalled");
        }
        finally
        {
            release.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposing_twice_releases_the_stop_source_only_after_the_running_loop_exits(bool stopFirst)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var ct = Ct;
        var tokens = new BlockingAdvertisementTokens(() =>
        {
            entered.TrySetResult();
            release.Wait(ct);
        });
        var store = new RouterAdvertisementStore(_ => throw new RedisException("down"), "caching:nodes");
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var advertiser = new RouterAdvertiser(
            store, "urn:test", "b", [tokens], TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(120), clock, NullLogger.Instance);
        var source = (CancellationTokenSource)typeof(RouterAdvertiser)
            .GetField("_stop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(advertiser)!;
        Action readToken = () => _ = source.Token;
        advertiser.Start();
        try
        {
            await entered.Task.WaitAsync(ct);
            if (stopFirst)
            {
                var stopping = advertiser.StopAsync(TimeSpan.Zero);
                clock.Advance(TimeSpan.Zero);
                await stopping.WaitAsync(ct);
            }

            advertiser.Dispose();
            advertiser.Dispose();
            await advertiser.StopAsync(TimeSpan.Zero);

            source.IsCancellationRequested.Should().BeTrue();
            readToken.Should().NotThrow("the advertisement loop is still running");
        }
        finally
        {
            release.Set();
        }

        await advertiser.Shutdown.WaitAsync(ct);
        readToken.Should().Throw<ObjectDisposedException>();
        advertiser.Dispose();
        await advertiser.StopAsync(TimeSpan.Zero);
    }

    [Fact]
    public async Task An_abandoned_acquisition_is_reused_not_doubled_and_its_fault_is_observed()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) => unobserved.AddRange(args.Exception.InnerExceptions);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var bound = TimeSpan.FromMilliseconds(100);
        var store = new RouterAdvertisementStore(
            _ =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult();
                release.Wait(Ct);
                throw new RedisException("released");
            },
            "caching:nodes");
        var entry = new RouterAdvertisement("n", "b", []);

        try
        {
            using var first = new CancellationTokenSource(bound, clock);
            var abandon = store.WriteAsync("f", entry, first.Token);
            await entered.Task.WaitAsync(Ct);
            clock.Advance(bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandon);

            using var second = new CancellationTokenSource(bound, clock);
            var again = store.WriteAsync("f", entry, second.Token);
            clock.Advance(bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => again);

            Volatile.Read(ref calls).Should().Be(1, "the running acquisition is reused while it is still running");
        }
        finally
        {
            release.Set();
        }

        var after = () => store.WriteAsync("f", entry, Ct);
        await after.Should().ThrowAsync<RedisException>();
        Volatile.Read(ref calls).Should().BeLessThanOrEqualTo(2);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
        unobserved.Should().NotContain(ex => ex.Message == "released", "the abandoned acquisition's fault is observed");
    }

    [Fact]
    public async Task Mode_change_nudges_keep_the_failure_backoff()
    {
        var attempts = 0;
        var clock = new SignalingTimeProvider(DateTimeOffset.UnixEpoch);
        var core = new RouterCore(clock, _ => true, NullLogger.Instance);
        var advertiser = Advertiser(
            () =>
            {
                Interlocked.Increment(ref attempts);
                throw new RedisException("down");
            },
            core,
            interval: TimeSpan.FromHours(1),
            clock: clock);
        advertiser.Start();
        await clock.WaitForTimerAsync(TimeSpan.FromMilliseconds(500), Ct);

        for (var i = 0; i < 30; i++)
        {
            advertiser.Nudge();
        }

        clock.Advance(TimeSpan.FromMilliseconds(499));
        await Task.Yield();
        Volatile.Read(ref attempts).Should().Be(1, "the first retry waits its 500 ms backoff whatever the nudges");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1), Ct);
        Volatile.Read(ref attempts).Should().Be(2);
        await advertiser.StopAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_loop_that_faults_is_logged_and_the_stop_never_throws()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var logger = new ErrorSignal();
        var core = new RouterCore(clock, _ => true, logger);
        var store = new RouterAdvertisementStore(_ => throw new RedisException("down"), "caching:nodes");
        var advertiser = new RouterAdvertiser(store, "urn:test", "b", [new BypassTokens(core)], TimeSpan.FromMilliseconds(-5), TimeSpan.FromSeconds(120), clock, logger);
        advertiser.Start();
        await logger.Logged.Task.WaitAsync(Ct);

        var stop = () => advertiser.StopAsync(TimeSpan.FromSeconds(1));

        await stop.Should().NotThrowAsync();
        advertiser.Stopped.Should().BeTrue();
        await stop.Should().NotThrowAsync("a second stop does nothing");
    }

    [Theory]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(5, 8000)]
    [InlineData(6, 15000)]
    [InlineData(40, 15000)]
    public void Backoff_doubles_up_to_the_interval(int failures, int expectedMilliseconds) =>
        RouterAdvertiser.Backoff(failures, TimeSpan.FromSeconds(15)).Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));

    [Fact]
    public void Entries_count_for_the_liveness_window_plus_two_intervals_and_are_pruned_later()
    {
        var advertiser = Advertiser(() => null!, new RouterCore(new ManualTimeProvider(DateTimeOffset.UnixEpoch), _ => true, NullLogger.Instance), interval: TimeSpan.FromSeconds(15));
        advertiser.Fresh.Should().Be(TimeSpan.FromSeconds(150));
        advertiser.Gone.Should().Be(TimeSpan.FromSeconds(300));
        advertiser.Field.Should().Be("urn:test" + RouterAdvertisement.FieldSuffix);
    }

    [Fact]
    public void The_node_list_key_is_the_one_the_stack_uses()
    {
        var stack = new RedisCacheOptions { RedisKeyStrategyFactory = PassThroughKeyStrategyFactory.Instance };
        RouterServiceCollectionExtensions.NodeListKey(new CacheOptions(), stack).Should().Be((RedisKey)"caching:nodes");
        RouterServiceCollectionExtensions.NodeListKey(new CacheOptions { AppShortName = "App", Separator = '$' }, new RedisCacheOptions())
            .Should().Be((RedisKey)"app$s$caching$nodes");
    }

    [Theory]
    [InlineData("urn:host", "urn:host-pabc?stack=UiPath.Caching.Migration")]
    [InlineData("urn:host?x=1", "urn:host-pabc?stack=UiPath.Caching.Migration")]
    public void The_identity_is_the_stack_form_then_the_process_part(string source, string expected) =>
        RouterServiceCollectionExtensions.StackIdentity(new Uri(source), "abc").ToString().Should().Be(expected);

    internal static RouterAdvertiser Advertiser(Func<IDatabase> database, RouterCore core, TimeSpan? interval = null, TimeSpan? liveness = null, TimeProvider? clock = null) =>
        new(
            new RouterAdvertisementStore(_ => ValueTask.FromResult(database()), "caching:nodes"),
            "urn:test",
            "test-build",
            [new BypassTokens(core)],
            interval ?? TimeSpan.FromSeconds(15),
            liveness ?? TimeSpan.FromSeconds(120),
            clock ?? core.Clock,
            NullLogger.Instance);

    private sealed class ErrorSignal : ILogger
    {
        public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Logged.TrySetResult();
            }
        }
    }

    private sealed class BlockingAdvertisementTokens(Action wait) : IAdvertisementTokens
    {
        public void AddTo(List<string> tokens) => wait();
    }
}
