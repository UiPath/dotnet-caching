using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class RegistrationCompletenessTests : RouterTestBase
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryAdd_creates_an_absent_key_then_evicts_and_publishes(bool library)
    {
        var node = NewNode(useLibrary: _ => library);
        var seen = new List<string>();
        Bus.OnPublish = ev => seen.Add($"{ev.Type}/{Store.ContainsKey("k")}");

        var outcome = await node.Cache.TryAddAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(AddOutcome.Added);
        Store["k"].Name.Should().Be("new");
        seen.Should().ContainSingle().Which.Should().EndWith("/True", "the event follows the commit");
        (library ? node.LibraryBackend : node.SourceBackend).Creates.Should().Be(1);
    }

    [Fact]
    public async Task TryAdd_on_a_held_key_changes_nothing_evicts_nothing_and_announces_nothing()
    {
        var node = NewNode();
        Store["k"] = new Item("old");
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeTrue();

        var outcome = await node.Cache.TryAddAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(AddOutcome.Exists);
        Store["k"].Name.Should().Be("old");
        node.IsLocal("k").Should().BeTrue();
        Bus.Published.Should().Be(0);
        node.SourceBackend.Creates.Should().Be(0, "an answered create is never retried on the other client");
    }

    [Fact]
    public async Task TryAdd_not_sent_on_the_library_is_sent_on_the_source()
    {
        var node = NewNode();
        node.LibraryBackend.CreateOverride = AddOutcome.NotSent;

        var outcome = await node.Cache.TryAddAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(AddOutcome.Added);
        node.SourceBackend.Creates.Should().Be(1);
    }

    [Fact]
    public async Task TryAdd_of_unknown_outcome_keeps_the_key_pending()
    {
        var node = NewNode(pendingWindow: TimeSpan.FromSeconds(30));
        node.LibraryBackend.CreateOverride = AddOutcome.Unknown;

        var outcome = await node.Cache.TryAddAsync("k", new Item("new"), default, Ct);

        outcome.Should().Be(AddOutcome.Unknown);
        Bus.Published.Should().Be(1);
        node.SourceBackend.Creates.Should().Be(0, "a create that may have landed is never resent");
        Store["k"] = new Item("landed");
        await node.Cache.TryGetAsync("k", Ct);
        node.IsLocal("k").Should().BeFalse("reads go to Redis while the create is pending");
    }

    [Fact]
    public async Task A_source_create_answering_failed_is_settled_and_reported_as_unknown()
    {
        var node = NewNode(useLibrary: _ => false, pendingWindow: TimeSpan.FromSeconds(30));
        node.SourceBackend.CreateOverride = AddOutcome.Failed;

        (await node.Cache.TryAddAsync("k", new Item("new"), default, Ct)).Should().Be(AddOutcome.Unknown);
        Bus.Published.Should().Be(1, "an unknown create is announced like a write of unknown outcome");
    }

    [Fact]
    public void A_version_provider_returning_null_is_refused()
    {
        var registration = new RouterCacheRegistration<Item>
        {
            Name = "items",
            Group = "g",
            Source = new FakeBackend([], Clock),
            RedisKey = key => key,
            Version = () => null!,
        };

        var act = () => new RouterBuilder().Add(registration);

        act.Should().Throw<ArgumentException>().WithMessage("*returned null*");
    }

    [Fact]
    public async Task TryAdd_with_a_deadline_already_passed_creates_nothing()
    {
        var node = NewNode();

        var outcome = await node.Cache.TryAddAsync("k", new Item("new"), CacheExpiry.At(Clock.GetUtcNow().AddSeconds(-1)), Ct);

        outcome.Should().Be(AddOutcome.NotSent);
        node.LibraryBackend.Creates.Should().Be(0);
        Store.Should().NotContainKey("k");
    }

    [Theory]
    [InlineData(AddOutcome.Added, AddOutcome.Added)]
    [InlineData(AddOutcome.Exists, AddOutcome.Exists)]
    [InlineData(AddOutcome.NotSent, AddOutcome.NotSent)]
    [InlineData(AddOutcome.Unknown, AddOutcome.Unknown)]
    [InlineData(AddOutcome.Failed, AddOutcome.Unknown)]
    public async Task The_library_client_reports_the_store_create(AddOutcome store, AddOutcome expected)
    {
        var cache = new FakeMigrationCache { Create = (_, _) => Task.FromResult(store) };
        var client = new LibraryPathClient<Item>(cache, new FakeConnection(), key => key, TimeSpan.FromSeconds(7));

        var result = await client.TryAddAsync("k", new Item("v"), null, Ct);

        result.Outcome.Should().Be(expected);
        if (expected == AddOutcome.Unknown)
        {
            result.SettleWithin.Should().Be(TimeSpan.FromSeconds(7));
        }
    }

    [Fact]
    public async Task A_versioned_cache_announces_under_its_versioned_name_and_keeps_its_logical_name_elsewhere()
    {
        var node = NewNode(version: () => "v3");
        node.Cache.Topic.Should().Be(new UiPath.Caching.Broadcast.TopicKey($"router:{CacheName}@v3"));

        await node.Cache.SetAsync("k", new Item("v"), default, Ct);

        Store["k"].Name.Should().Be("v", "the key map, not the router, puts the version in the key");
        node.IsLocal("k").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("v@3")]
    [InlineData("v+3")]
    [InlineData("v 3")]
    [InlineData("{v3}")]
    public void A_version_that_would_break_the_name_grammar_is_refused(string version)
    {
        var registration = new RouterCacheRegistration<Item>
        {
            Name = "items",
            Group = "g",
            Source = new FakeBackend([], Clock),
            RedisKey = key => key,
            Version = () => version,
        };

        var act = () => new RouterBuilder().Add(registration);

        act.Should().Throw<ArgumentException>().WithMessage("*version*");
    }

    [Fact]
    public async Task A_cached_null_at_the_source_is_a_hit_and_is_kept_locally()
    {
        var node = NewNode(useLibrary: _ => false, cacheNulls: true);
        node.SourceBackend.NullKeys.Add("k");
        var runs = 0;

        var first = await node.Cache.GetOrAddAsync("k", _ => { runs++; return Task.FromResult<Item?>(new Item("made")); }, default, Ct);
        var second = await node.Cache.TryGetAsync("k", Ct);

        first.Should().BeNull();
        second.Should().BeNull();
        runs.Should().Be(0, "a cached null is a hit, so the factory does not run");
        node.SourceBackend.Reads.Should().Be(1, "the second read is a local hit");
        node.IsLocal("k").Should().BeTrue();
    }

    [Fact]
    public async Task Without_CacheNulls_a_cached_null_is_a_miss()
    {
        var node = NewNode(useLibrary: _ => false);
        node.SourceBackend.NullKeys.Add("k");
        var runs = 0;

        var value = await node.Cache.GetOrAddAsync("k", _ => { runs++; return Task.FromResult<Item?>(new Item("made")); }, default, Ct);

        value!.Name.Should().Be("made");
        runs.Should().Be(1);
    }

    [Theory]
    [InlineData(true, ReadOutcome.Found)]
    [InlineData(false, ReadOutcome.Absent)]
    public async Task The_library_client_reads_a_stored_null_as_found_only_with_CacheNulls(bool cacheNulls, ReadOutcome expected)
    {
        var cache = new FakeMigrationCache { Read = (_, _) => Task.FromResult<object?>(FakeMigrationCache.StoredNull) };
        var client = new LibraryPathClient<Item>(cache, new FakeConnection(), key => key, TimeSpan.FromSeconds(1)) { CacheNulls = cacheNulls };

        var read = await client.ReadAsync("k", Ct);

        read.Kind.Should().Be(expected);
        read.Value.Should().BeNull();
    }
}
