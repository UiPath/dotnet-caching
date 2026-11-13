using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;

namespace UiPath.Caching.Tests.Negotiation;

public class NodeAdvertisementTests
{
    private static readonly RedisKey Key = "ads";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Ad_round_trips_formats_version_and_strict_lists()
    {
        var ad = new NodeAdvertisement(
            "3.2.0",
            "9f1c",
            ["json", "02", "03", "10"],
            new Dictionary<string, IReadOnlyList<string>> { ["a;b:c"] = ["json", "02"], ["plain"] = ["json"] },
            Migration: "orders:dualdelete",
            Left: true);

        NodeAdvertisement.TryParse(ad.Encode(), out var parsed).Should().BeTrue();

        parsed!.Library.Should().Be("3.2.0");
        parsed.Instance.Should().Be("9f1c");
        parsed.ValueReads.Should().Equal("json", "02", "03", "10");
        parsed.Strict.Should().BeEquivalentTo(ad.Strict);
        parsed.Migration.Should().Be("orders:dualdelete");
        parsed.Left.Should().BeTrue();
    }

    [Fact]
    public void An_ad_that_is_not_leaving_does_not_say_so()
    {
        var ad = new NodeAdvertisement("3.2.0", "i", ["json"], new Dictionary<string, IReadOnlyList<string>>());

        ad.Encode().Should().NotContain("left");
        NodeAdvertisement.TryParse(ad.Encode(), out var parsed).Should().BeTrue();
        parsed!.Left.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_field_is_skipped_and_a_text_without_reads_is_no_ad()
    {
        NodeAdvertisement.TryParse("lib=3.3.0;inst=x;vr=json,02;er=json,02;sub=c:1", out var parsed).Should().BeTrue();
        parsed!.ValueReads.Should().Equal("json", "02");
        parsed.Migration.Should().BeNull();

        NodeAdvertisement.TryParse("lib=3.3.0;inst=x", out _).Should().BeFalse();
        NodeAdvertisement.TryParse("garbage", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ad_key_is_built_like_the_maintainers_lock_key_and_carries_its_hash_tag(bool shardKeys)
    {
        var cacheOptions = new CacheOptions { AppShortName = "app" };
#pragma warning disable CS0618 // The shard strategy is the one a cluster app sets.
        cacheOptions.ShardKeyEnabled = shardKeys;
#pragma warning restore CS0618
        var redisOptions = new RedisCacheOptions();
        var maintainer = new RedisStreamHealthMaintainer(
            Substitute.For<IRedisConnector>(),
            Substitute.For<UiPath.Caching.Telemetry.ICachingTelemetryProvider>(),
            Options.Create(new RedisStreamsTopicOptions()),
            Options.Create(redisOptions),
            Options.Create(cacheOptions),
            NullLogger<RedisStreamHealthMaintainer>.Instance,
            TimeProvider.System);
        maintainer.Initialize();

        var key = NodeListStore.KeyFor(cacheOptions, redisOptions).ToString();
        var lockKey = maintainer.LockKey.ToString();

        var prefix = key[..key.IndexOf("caching", StringComparison.Ordinal)];
        lockKey.Should().StartWith(prefix, "the ad key is built by the lock key's strategy, so the keyspace and tag rules are the same");
        prefix.Should().Be(shardKeys ? "app:s:{" : "app:s:");
        RedisHashTag.HasValidTag(key).Should().Be(shardKeys);
        RedisHashTag.HasValidTag(lockKey).Should().Be(shardKeys);
    }

    [Fact]
    public async Task Age_uses_server_time_not_local_clock()
    {
        var server = new FakeNodeListServer { NowMs = 946_684_800_000 };
        var store = new NodeListStore(server.Connector, Key);

        await store.WriteAsync("node-a", Ad(), Ct);
        server.NowMs += 10_000;
        var ads = await store.ReadAsync(Ct);

        // A stamp from this machine's clock would be decades away from the server's, and the age with it.
        ads.Should().ContainSingle().Which.Age.Should().Be(TimeSpan.FromSeconds(10));
        ads[0].Stamp.Should().Be(946_684_800_000);
        server.Hash["node-a"].Should().StartWith("946684800000|");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Works_with_scripts_disabled(bool scripts)
    {
        var server = new FakeNodeListServer(scripts);
        var store = new NodeListStore(server.Connector, Key);

        await store.WriteAsync("node-a", Ad(), Ct);
        server.NowMs += 4_000;
        await store.WriteAsync("node-b", Ad(), Ct);
        server.NowMs += 1_000;
        var ads = await store.ReadAsync(Ct);

        ads.Should().HaveCount(2);
        ads.Single(a => a.NodeId == "node-a").Age.Should().Be(TimeSpan.FromSeconds(5));
        ads.Single(a => a.NodeId == "node-b").Age.Should().Be(TimeSpan.FromSeconds(1));
        ads[0].Advertisement.ValueReads.Should().Equal("json", "02");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prune_removes_only_an_ad_that_still_holds_the_stamp_it_was_read_with(bool scripts)
    {
        var server = new FakeNodeListServer(scripts);
        var store = new NodeListStore(server.Connector, Key);
        await store.WriteAsync("old", Ad(), Ct);
        await store.WriteAsync("raced", Ad(), Ct);
        server.NowMs += 600_000;
        await store.WriteAsync("fresh", Ad(), Ct);
        var read = await store.ReadAsync(Ct);

        // "raced" advertised again between the read and the prune.
        server.NowMs += 1_000;
        await store.WriteAsync("raced", Ad(), Ct);
        await store.PruneAsync([.. read.Where(a => a.Age > TimeSpan.FromMinutes(5))], Ct);

        (await store.ReadAsync(Ct)).Select(a => a.NodeId).Should().BeEquivalentTo(["raced", "fresh"]);
    }

    [Fact]
    public async Task Fields_that_do_not_parse_are_left_out()
    {
        var server = new FakeNodeListServer();
        var store = new NodeListStore(server.Connector, Key);
        server.Put("junk", "not-a-stamp|vr=json");
        server.Put("other", "1|no reads here");
        await store.WriteAsync("fresh", Ad(), Ct);

        (await store.ReadAsync(Ct)).Select(a => a.NodeId).Should().Equal("fresh");
    }

    [Fact]
    public async Task A_transient_server_error_is_not_a_script_refusal_and_scripts_are_tried_again()
    {
        var server = new FakeNodeListServer();
        var store = new NodeListStore(server.Connector, Key);
#pragma warning disable CS0618 // The only public constructor of a server error.
        server.Failure = new RedisServerException("READONLY You can't write against a read only replica.");
#pragma warning restore CS0618

        await FluentActions.Awaiting(() => store.WriteAsync("a", Ad(), Ct)).Should().ThrowAsync<RedisServerException>();
        server.Failure = null;
        await store.WriteAsync("a", Ad(), Ct);

        server.Events.Should().Equal("write");
        await server.Database.DidNotReceive().ExecuteAsync("TIME", Arg.Any<ICollection<object>?>(), Arg.Any<CommandFlags>());
    }

    [Theory]
    [InlineData("NOPERM this user has no permissions to run the 'eval' command")]
    [InlineData("ERR unknown command 'EVAL'")]
    [InlineData("NOSCRIPT No matching script.")]
    public void A_refusal_to_run_scripts_is_told_from_a_transient_error(string message)
    {
#pragma warning disable CS0618 // The only public constructor of a server error.
        NodeListStore.IsScriptRefusal(new RedisServerException(message)).Should().BeTrue();
        NodeListStore.IsScriptRefusal(new RedisServerException("LOADING Redis is loading the dataset in memory")).Should().BeFalse();
        NodeListStore.IsScriptRefusal(new RedisServerException("BUSY Redis is busy running a script.")).Should().BeFalse();
        NodeListStore.IsScriptRefusal(new RedisServerException("READONLY You can't write against a read only replica.")).Should().BeFalse();
#pragma warning restore CS0618
    }

    private static NodeAdvertisement Ad() => new("3.2.0", "i", ["json", "02"], new Dictionary<string, IReadOnlyList<string>>());
}
