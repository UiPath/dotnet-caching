using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A routed hash cache over the key move client: in each state of a short walk the router reads what the state before wrote, and every write lands where the state puts it.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashRouterTests(RedisFixture fixture)
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_walk_through_S1_S3_and_S5_loses_no_field_and_every_write_lands_on_the_side_of_its_state()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: clock);
        await using var node = HashTestNode.Create(clock, "kmh" + kit.Kit.Unique, library: kit.Client);
        var cache = node.Surface;
        await kit.Kit.PrepareAsync("k");

        await kit.Kit.SetStateAsync("S1", nPop: 0);
        (await cache.SetAsync("k", Items(("a", "1"), ("b", "2")), Life, Ct)).Should().BeTrue();
        (await cache.SetAsync("k", Items(("c", "3")), new HashCacheEntryOptions(TimeToLive: Life, SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" });
        (await cache.GetAsync("k", Ct)).Keys.Should().BeEquivalentTo("a", "b", "c");
        (await cache.RefreshAsync("k", TimeSpan.FromHours(2), Ct)).Should().BeTrue();
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));

        await kit.Kit.SetStateAsync("S3");
        (await cache.GetAsync("k", Ct)).Keys.Should().BeEquivalentTo(["a", "b", "c"], "the read promotes the hash it finds in L");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeTrue();
        (await cache.SetAsync("k", Items(("d", "4")), new HashCacheEntryOptions(TimeToLive: Life, SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        (await cache.RemoveFieldsAsync("k", ["a"], Ct)).Should().BeTrue();
        (await kit.FieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("b", "c", "d");
        (await cache.GetItemAsync("k", "d", Ct))!.Name.Should().Be("4");

        await kit.Kit.SetStateAsync("S5", swept: true);
        (await cache.GetAsync("k", Ct)).Keys.Should().BeEquivalentTo("b", "c", "d");
        (await cache.SetAsync("k", Items(("e", "5")), new HashCacheEntryOptions(TimeToLive: Life, SetOption: HashCacheSetOption.HashReplace), Ct)).Should().BeTrue();
        (await kit.FieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("b", "c", "d", "e");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await cache.RemoveAsync("k", Ct)).Should().BeTrue();
        (await cache.GetAsync("k", Ct)).Should().BeEmpty();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_miss_fill_in_S3_stores_what_the_generator_made_in_N_and_a_fill_that_lost_returns_the_winner()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: clock);
        await using var node = HashTestNode.Create(clock, "kmh" + kit.Kit.Unique, library: kit.Client);
        await kit.Kit.PrepareAsync("made", "raced");
        await kit.Kit.SetStateAsync("S3");

        var made = await node.Surface.GetOrAddAsync("made", _ => Task.FromResult<IDictionary<string, Item?>>(Items(("a", "1"))), Life, Ct);
        var raced = await node.Surface.GetOrAddAsync(
            "raced",
            async _ =>
            {
                await kit.SetAsync(kit.N("raced"), new Dictionary<string, string> { ["w"] = "win" });
                return (IDictionary<string, Item?>)Items(("mine", "x"));
            },
            Life,
            Ct);

        made.Keys.Should().BeEquivalentTo("a");
        (await kit.FieldsAsync(kit.N("made"))).Keys.Should().BeEquivalentTo("a");
        raced.Keys.Should().BeEquivalentTo("w");
        (await kit.FieldsAsync(kit.N("raced"))).Keys.Should().BeEquivalentTo("w");
    }

    [Fact]
    public async Task A_write_with_metadata_is_refused_where_the_in_place_format_has_no_field_for_it_and_the_hash_is_not_touched()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, clock: clock);
        await using var node = HashTestNode.Create(clock, "kmh" + kit.Kit.Unique, library: kit.Client);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);

        var write = async () => await node.Surface.SetAsync("k", Items(("a", "1")), new HashCacheEntryOptions(Metadata: new Dictionary<string, string?> { ["m"] = "x" }), Ct);

        await write.Should().ThrowAsync<NotSupportedException>();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        node.Source.Writes.Should().Be(0);
    }

    // A refresh that names no life takes the default of the registration, and with none it removes the life; the key move client takes a life only as a date, so the router never hands it neither.
    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S2", true)]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    [InlineData("S4", false)]
    [InlineData("S5", true)]
    [InlineData("S5", false)]
    public async Task A_refresh_in_every_state_takes_the_default_life_the_explicit_one_or_none(string state, bool scripts)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, clock: clock);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: state is "S1" or "S2" ? 0 : 1, swept: state == "S5");
        var side = state is "S1" or "S2" ? kit.L("k") : kit.N("k");
        await using var none = HashTestNode.Create(clock, "kmh" + kit.Kit.Unique, library: kit.Client);
        await using var configured = HashTestNode.Create(clock, "kmc" + kit.Kit.Unique, library: kit.Client, defaultExpiry: TimeSpan.FromHours(2));
        await kit.SetAsync(side, new Dictionary<string, string> { ["a"] = "1" }, TimeSpan.FromMinutes(1));

        (await none.Surface.RefreshAsync("k", Ct)).Should().BeTrue("no default life and no life given");
        var endless = await kit.Db.KeyTimeToLiveAsync(side);
        (endless is null || endless > TimeSpan.FromDays(365)).Should().BeTrue("no life is a life of no end, not the minute it had");
        (await none.Surface.RefreshAsync("k", new HashCacheEntryOptions(), Ct)).Should().BeTrue("an options refresh that names neither life nor metadata");
        (await configured.Surface.RefreshAsync("k", Ct)).Should().BeTrue();
        (await kit.Db.KeyTimeToLiveAsync(side))!.Value.Should().BeCloseTo(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1));
        (await none.Surface.RefreshAsync("k", TimeSpan.FromHours(3), Ct)).Should().BeTrue();
        (await kit.Db.KeyTimeToLiveAsync(side))!.Value.Should().BeCloseTo(TimeSpan.FromHours(3), TimeSpan.FromMinutes(1));
        (await configured.Surface.RefreshAsync("k", DateTimeOffset.UtcNow.AddHours(4), Ct)).Should().BeTrue();
        (await kit.Db.KeyTimeToLiveAsync(side))!.Value.Should().BeCloseTo(TimeSpan.FromHours(4), TimeSpan.FromMinutes(1));
    }

    private static Dictionary<string, Item?> Items(params (string Field, string? Value)[] fields) => KeyMoveHashKit.Items(fields);
}
