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
        await using var node = TestNode.Create(new FakeBus(), new ManualTimeProvider(DateTimeOffset.UtcNow), [], "c");
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
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
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
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref attempts) < 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        var stop = () => advertiser.StopAsync(TimeSpan.FromSeconds(1));
        await stop.Should().NotThrowAsync();
        Volatile.Read(ref attempts).Should().BeGreaterThanOrEqualTo(4, "the departing write is attempted too, and nothing threw");
    }

    [Fact]
    public async Task A_stalled_connection_never_holds_the_stop()
    {
        using var never = new ManualResetEventSlim();
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        var advertiser = Advertiser(
            () =>
            {
                never.Wait(TimeSpan.FromSeconds(30));
                throw new RedisException("released");
            },
            core);
        advertiser.Start();
        await Task.Delay(100, Ct);

        var stopping = advertiser.StopAsync(TimeSpan.FromMilliseconds(200));

        (await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5), Ct))).Should().BeSameAs(stopping, "the stop is bounded even while acquisition blocks");
        never.Set();
    }

    [Fact]
    public async Task Mode_change_nudges_keep_the_failure_backoff()
    {
        var attempts = 0;
        var core = new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance);
        var advertiser = Advertiser(
            () =>
            {
                Interlocked.Increment(ref attempts);
                throw new RedisException("down");
            },
            core,
            interval: TimeSpan.FromHours(1));
        advertiser.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref attempts) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5, Ct);
        }

        for (var i = 0; i < 30; i++)
        {
            advertiser.Nudge();
            await Task.Delay(5, Ct);
        }

        Volatile.Read(ref attempts).Should().Be(1, "the first retry waits its 500 ms backoff whatever the nudges");
        await advertiser.StopAsync(TimeSpan.FromSeconds(1));
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
        var advertiser = Advertiser(() => null!, new RouterCore(TimeProvider.System, _ => true, NullLogger.Instance), interval: TimeSpan.FromSeconds(15));
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

    internal static RouterAdvertiser Advertiser(Func<IDatabase> database, RouterCore core, TimeSpan? interval = null, TimeSpan? liveness = null) =>
        new(
            new RouterAdvertisementStore(_ => ValueTask.FromResult(database()), "caching:nodes"),
            "urn:test",
            "test-build",
            [new BypassTokens(core)],
            interval ?? TimeSpan.FromSeconds(15),
            liveness ?? TimeSpan.FromSeconds(120),
            TimeProvider.System,
            NullLogger.Instance);
}
