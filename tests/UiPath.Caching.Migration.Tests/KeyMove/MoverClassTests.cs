using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// T-B15, the class part: what the mover moves and what it leaves, with exact counters, on the real scripts.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverClassTests(RedisFixture fixture)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_accepted_class_is_left_in_L_and_counted_exactly_and_the_rest_moves()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(new Dictionary<string, (string, TimeSpan?)>
        {
            ["ok"] = ("v", null),
            ["ok2"] = ("v2", Hour),
            ["short"] = ("v", TimeSpan.FromMinutes(1)),
            ["big:1"] = ("big:1", null),
            ["exp:1"] = ("exp:1", null),
            ["junk:1"] = ("junk:1", null),
            ["weird:1"] = ("v", null),
        });

        await kit.RunOnePassAsync(kit.Host());

        (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long>
        {
            ["moved"] = 2,
            ["skipped:short_ttl"] = 1,
            ["skipped:too_large"] = 1,
            ["skipped:expired"] = 1,
            ["skipped:unreadable"] = 1,
            ["skipped:unmappable"] = 1,
        });
        (await kit.Kit.GetAsync(kit.Kit.N("ok"))).Should().Be("v");
        (await kit.Db.KeyTimeToLiveAsync(kit.Kit.N("ok"))).Should().BeNull("a key with no expiry moves with none");
        (await kit.Db.KeyTimeToLiveAsync(kit.Kit.N("ok2")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(55));
        foreach (var left in new[] { "short", "big:1", "exp:1", "junk:1", "weird:1" })
        {
            (await kit.Kit.ExistsAsync(kit.Kit.L(left))).Should().BeTrue(left);
            (await kit.Kit.ExistsAsync(kit.Kit.N(left))).Should().BeFalse(left);
        }

        (await kit.Kit.ExistsAsync(kit.Kit.L("ok"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.Kit.L("ok2"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, false, 1, "short_ttl")]
    [InlineData(0, null, false, 1, "moved")]
    [InlineData(null, 0, false, 1, "moved")]
    [InlineData(120, null, false, 1, "short_ttl")]
    [InlineData(120, 0, false, 1, "moved")]
    [InlineData(0, 120, false, 1, "short_ttl")]
    [InlineData(null, null, true, 1, "moved")]
    [InlineData(null, 120, true, 1, "moved")]
    public async Task MoveMinTtl_comes_from_the_group_and_the_cache_overrides_it_and_zero_moves_all_and_a_NoOrigin_cache_ignores_it(int? groupMinutes, int? cacheMinutes, bool noOrigin, int ttlMinutes, string expected)
    {
        await using var kit = await MoverKit.CreateAsync(
            fixture,
            noOrigin: noOrigin,
            groupMin: groupMinutes is { } g ? TimeSpan.FromMinutes(g) : null,
            cacheMin: cacheMinutes is { } c ? TimeSpan.FromMinutes(c) : null);
        await kit.SeedAsync(new Dictionary<string, (string, TimeSpan?)> { ["k"] = ("v", TimeSpan.FromMinutes(ttlMinutes)) });

        await kit.RunOnePassAsync(kit.Host());

        var progress = await kit.ProgressAsync();
        progress.Should().ContainKey(expected == "moved" ? "moved" : "skipped:" + expected).WhoseValue.Should().Be(1);
        progress.Should().HaveCount(1);
        (await kit.Kit.ExistsAsync(kit.Kit.N("k"))).Should().Be(expected == "moved");
    }

    [Fact]
    public async Task A_key_that_N_already_holds_drains_L_without_replacing_N_and_is_not_counted_as_a_duplicate()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("dup");
        await kit.Db.StringSetAsync(kit.Kit.N("dup"), "n-value");

        await kit.RunOnePassAsync(kit.Host());

        (await kit.Kit.ExistsAsync(kit.Kit.L("dup"))).Should().BeFalse("the compare-delete runs even when the publish answers 2");
        (await kit.Kit.GetAsync(kit.Kit.N("dup"))).Should().Be("n-value");
        (await kit.ProgressAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_that_L_no_longer_holds_when_the_source_is_read_is_counted_as_a_duplicate()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync("gone", "kept");

        // A reader promoted the key between the scan and the mover's snapshot.
        await kit.RunOnePassAsync(kit.Host(wrap: inner => new DeleteBeforeTarget(inner, kit.Db, kit.Kit.L("gone"), "gone")));

        (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 1, ["skipped:duplicate"] = 1 });
        (await kit.Target.MoveAsync("never-there", TimeSpan.Zero, kit.Kit.Id, AlwaysOwned.Instance, Ct)).SkipReason.Should().Be("duplicate");
    }

    [Fact]
    public async Task A_key_two_caches_own_is_left_untouched_and_counted_with_one_alert_for_the_pass()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, twoOwners: true);
        await kit.SeedAsync("a1", "s1", "s2", "b1");

        await kit.RunOnePassAsync(kit.Host());

        (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 1, ["skipped:ambiguous"] = 2 });
        foreach (var held in new[] { "s1", "s2", "b1" })
        {
            (await kit.Kit.ExistsAsync(kit.Kit.L(held))).Should().BeTrue(held);
            (await kit.Kit.ExistsAsync(kit.Kit.N(held))).Should().BeFalse(held);
        }

        kit.Kit.Plane.Alerts.Count(a => a.Code == "ambiguous_ownership").Should().Be(1);
    }

    [Fact]
    public async Task A_key_of_a_cache_the_app_uses_directly_is_never_touched_or_counted()
    {
        await using var kit = await MoverKit.CreateAsync(fixture, excluded: "km4:{U}:L:direct:*");
        await kit.SeedAsync("direct:1", "mine");

        await kit.RunOnePassAsync(kit.Host());

        (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 1 });
        (await kit.Kit.GetAsync(kit.Kit.L("direct:1"))).Should().Be("v-direct:1");
        (await kit.Kit.ExistsAsync(kit.Kit.N("direct:1"))).Should().BeFalse();
    }

    [Fact]
    public async Task The_mover_reaches_L_only_through_the_move_side()
    {
        RecordingMoveSide? side = null;
        KeyRecordingDatabase? recorder = null;
        await using var kit = await MoverKit.CreateAsync(
            fixture,
            source: k => side = new RecordingMoveSide(new InPlaceMoveSide<Item>(k.Kit.SidePlane, k.Kit.Group, k.Kit.L, ItemCodec.Instance, null, k.Kit.Clock)),
            interceptDb: db => KeyRecordingDatabase.Over(db, out recorder));
        await kit.SeedAsync("a", "b");

        await kit.RunOnePassAsync(kit.Host());

        side!.Calls.Should().Contain(["Snapshot", "CompareDelete"]);
        (await kit.ProgressAsync()).Should().BeEquivalentTo(new Dictionary<string, long> { ["moved"] = 2 });
        recorder!.Touched.Should().Contain(kit.Kit.N("a"));
        recorder.Touched.Where(t => t.Contains(":L:", StringComparison.Ordinal)).Should().BeEmpty();
    }

    // Deletes a key from L just before the mover looks at it, as a read-through promotion would.
    private sealed class DeleteBeforeTarget(IMoveTarget inner, StackExchange.Redis.IDatabase db, string redisKey, string logical) : IMoveTarget
    {
        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
        {
            if (key == logical)
            {
                await db.KeyDeleteAsync(redisKey);
            }

            return await inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
        }
    }
}
