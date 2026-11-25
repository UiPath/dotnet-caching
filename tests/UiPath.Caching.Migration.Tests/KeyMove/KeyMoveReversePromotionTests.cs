using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The reverse read-through, the reverse fills and the reverse creates of a string cache.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveReversePromotionTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_read_serves_N_and_promotes_it_to_L_in_its_own_format(bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v1", TimeSpan.FromMinutes(10));

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value!.Name.Should().Be("v1");
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the promoter deletes N once L holds the value");
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
        (await kit.FenceAsync("k")).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_read_preserves_the_absence_of_a_TTL(bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v1");

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("v1");
        read.Expiration.Should().BeNull();
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k"))).Should().BeNull();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_S3_rev_read_of_a_cached_null_on_N_returns_absent_and_promotes_nothing(bool duringPromotion)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, native: new ItemCodec("null:"u8.ToArray()));
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.L("k"), "older");
        if (duringPromotion)
        {
            await kit.Db.StringSetAsync(kit.N("k"), "current");
            kit.Schedule.Hook = async (step, _) =>
            {
                if (step == SchedulePointStep.ReadSource)
                {
                    await kit.Db.StringSetAsync(kit.N("k"), "null:");
                }
            };

            (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("current");
            kit.Schedule.Reset();
        }
        else
        {
            await kit.Db.StringSetAsync(kit.N("k"), "null:");
        }

        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Absent);

        (await kit.GetAsync(kit.N("k"))).Should().Be("null:");
        (await kit.GetAsync(kit.L("k"))).Should().Be("older");
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.ReadDestinationOnMaster);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_S3_rev_batch_read_promotes_N_hits_and_reads_L_for_misses(bool includeMisses)
    {
        await using var kit = await CreateAsync(scripts: true);
        string[] hits = ["a", "b", "c"];
        string[] keys = includeMisses ? [.. hits, "legacy", "missing"] : hits;
        await kit.PrepareAsync(keys);
        await kit.SetStateAsync("S3", direction: "rev");
        foreach (var key in hits)
        {
            await kit.Db.StringSetAsync(kit.N(key), "n:" + key);
        }

        if (includeMisses)
        {
            await kit.Db.StringSetAsync(kit.L("legacy"), "l:older");
        }

        var reads = await kit.Client.ReadManyAsync(keys, Ct);

        foreach (var key in hits)
        {
            reads[key].Value!.Name.Should().Be(key);
            (await kit.GetAsync(kit.L(key))).Should().Be("l:" + key);
            (await kit.ExistsAsync(kit.N(key))).Should().BeFalse();
        }

        if (includeMisses)
        {
            reads["legacy"].Value!.Name.Should().Be("older");
            reads["missing"].Kind.Should().Be(ReadOutcome.Absent);
        }

        kit.Schedule.Steps.Should().NotContain(SchedulePointStep.ReadDestination);
        kit.Hints().Count.Should().Be(0);
    }

    // The direction leaves the forward run as it was: L to N in the native format under G, an nx publish, then the delete of L.
    [Fact]
    public async Task A_forward_promotion_is_unchanged_by_the_reverse_direction()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v1");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v1");

        kit.Schedule.Steps.Should().Equal(
            SchedulePointStep.ReadDestination,
            SchedulePointStep.ReadDestinationOnMaster,
            SchedulePointStep.ProbeSource,
            SchedulePointStep.Capture,
            SchedulePointStep.ReadSource,
            SchedulePointStep.Publish,
            SchedulePointStep.ConfirmDestination,
            SchedulePointStep.CompareDelete);
        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.FenceAsync("k")).Should().NotBeNull();
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_forward_promotion_still_publishes_only_if_N_is_absent()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v1");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.Db.StringSetAsync(kit.N("k"), "n:v9");
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v9");

        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v9");
    }

    [Fact]
    public async Task An_S3_rev_promotion_replaces_an_older_copy_in_L_because_N_is_the_authority()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v2");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v1");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v2");

        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v2");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_read_that_misses_N_reads_L_and_changes_nothing(bool present)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        if (present)
        {
            await kit.Db.StringSetAsync(kit.L("k"), "l:v1");
        }

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(present ? ReadOutcome.Found : ReadOutcome.Absent);
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.ReadDestinationOnMaster, SchedulePointStep.ReadSource);
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeFalse("a miss captures no fence");
        kit.Hints().Count.Should().Be(0);
    }

    [Theory]
    [InlineData("S1", "follow")]
    [InlineData("S4", "follow")]
    [InlineData("S3", "blind")]
    public async Task A_reverse_read_promotes_nothing_outside_S3_rev_and_under_the_blind_rule(string state, string reads)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        if (reads == "blind")
        {
            kit.Plane.Rules = kit.Plane.Rules with { Reads = ReadRule.UnionNativeFirst };
        }

        await kit.Db.StringSetAsync(kit.N("k"), "n:v1");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v1");

        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_reverse_promotion_whose_fence_moves_before_the_publish_starts_again_and_finishes()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v1");
        var moved = false;
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish && !moved)
            {
                moved = true;
                await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "other");
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v1");

        kit.Schedule.Steps.Count(s => s == SchedulePointStep.Capture).Should().Be(2);
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_value_the_in_place_format_cannot_hold_is_served_from_N_and_left_there()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:" + TaggedItemCodec.Unencodable);

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be(TaggedItemCodec.Unencodable);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeTrue();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task An_oversized_readable_N_value_remains_authoritative_after_a_reverse_read()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "big:current");
        await kit.Db.StringSetAsync(kit.L("k"), "older");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("big:current");
        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("big:current");
        (await kit.GetAsync(kit.N("k"))).Should().Be("big:current");
        (await kit.GetAsync(kit.L("k"))).Should().Be("older");
    }

    [Fact]
    public async Task Reverse_promotion_preserves_an_unreadable_N_value_above_the_size_cap()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, options: new KeyMoveOptions { MaxValueBytes = 8 });
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "current");
        await kit.Db.StringSetAsync(kit.L("k"), "older");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.ReadSource)
            {
                await kit.Db.StringSetAsync(kit.N("k"), "junk:oversized");
            }
        };

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("current");

        (await kit.GetAsync(kit.N("k"))).Should().Be("junk:oversized");
        (await kit.GetAsync(kit.L("k"))).Should().Be("older");
    }

    [Fact]
    public async Task An_N_value_with_an_L_tag_is_unreadable_and_does_not_promote()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "l:v1");

        (await kit.Client.ReadAsync("k", Ct)).Kind.Should().Be(ReadOutcome.Unreadable);

        (await kit.GetAsync(kit.N("k"))).Should().Be("l:v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task An_S3_rev_read_of_a_NoOrigin_cache_serves_N_without_promoting()
    {
        await using var kit = await CreateAsync(scripts: true, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v1");

        (await kit.Client.ReadAsync("k", Ct)).Value!.Name.Should().Be("v1");

        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S1_rev_fill_creates_N_under_the_forward_fence(bool fenceBefore)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", direction: "rev");
        if (fenceBefore)
        {
            await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "before");
        }

        (await kit.Client.FillAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.FenceAsync("k")).Should().NotBeNull().And.NotBe("before", "a forward promoter on a lagging node is refused");
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task An_S3_and_S4_rev_fill_creates_L_in_its_own_format_under_the_reverse_fence(string state)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");

        (await kit.Client.FillAsync("k", new Item("v1"), null, null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("S1", "n:", true)]
    [InlineData("S3", "l:", false)]
    public async Task A_reverse_fill_that_loses_writes_nothing_and_returns_the_stored_value(string state, string tag, bool onN)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        var holder = onN ? kit.N("k") : kit.L("k");
        await kit.Db.StringSetAsync(holder, tag + "v0");
        var box = new FillBox<Item>();

        (await kit.Client.FillAsync("k", new Item("v1"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        box.Lost.Should().BeTrue();
        box.Stored!.Name.Should().Be("v0");
        (await kit.GetAsync(holder)).Should().Be(tag + "v0");
    }

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    [InlineData("S4", false)]
    public async Task Reverse_states_close_fill_and_create_for_a_NoOrigin_cache(string state, bool fill)
    {
        await using var kit = await CreateAsync(scripts: true, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");

        Func<Task> write = fill
            ? async () => await kit.Client.FillAsync("k", new Item("v1"), null, null, Ct)
            : async () => await kit.Client.TryAddAsync("k", new Item("v1"), null, Ct);

        await write.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        kit.Schedule.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task An_S1_rev_create_fills_N_and_then_deletes_L_under_the_reverse_fence()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", direction: "rev");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Fill)
            {
                await kit.Db.StringSetAsync(kit.L("k"), "l:late");
            }
        };

        (await kit.Client.TryAddAsync("k", new Item("v1"), TimeSpan.FromMinutes(10), Ct)).Kind.Should().Be(TryAddKind.Created);

        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.FenceAsync("k")).Should().NotBeNull();
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
        kit.Schedule.Steps.Should().EndWith([SchedulePointStep.Fill, SchedulePointStep.Bump]);
    }

    [Fact]
    public async Task An_S1_rev_create_whose_trailing_step_is_refused_still_holds_its_claim()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", direction: "rev");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                await kit.AdvanceRecordsAsync("k", 99, "newer");
            }
        };

        (await kit.Client.TryAddAsync("k", new Item("v1"), null, Ct)).Kind.Should().Be(TryAddKind.Created);

        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        kit.Schedule.Steps.Count(s => s == SchedulePointStep.Fill).Should().Be(1, "a rerun would find its own claim and answer false");
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task An_S3_and_S4_rev_create_writes_L_in_its_own_format(string state)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");

        (await kit.Client.TryAddAsync("k", new Item("v1"), TimeSpan.FromMinutes(10), Ct)).Kind.Should().Be(TryAddKind.Created);

        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("S1", "N")]
    [InlineData("S1", "L")]
    [InlineData("S3", "N")]
    [InlineData("S3", "L")]
    [InlineData("S4", "N")]
    public async Task A_reverse_create_finds_a_value_on_either_side_and_answers_false(string state, string side)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        await kit.Db.StringSetAsync(side == "N" ? kit.N("k") : kit.L("k"), (side == "N" ? "n:" : "l:") + "v0");

        var result = await kit.Client.TryAddAsync("k", new Item("v1"), null, Ct);

        result.Kind.Should().Be(TryAddKind.Exists);
        result.Existing!.Name.Should().Be("v0");
    }

    [Fact]
    public async Task An_S3_rev_create_that_finds_a_key_made_after_its_capture_answers_false_and_writes_nothing()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.ProbeSource)
            {
                await kit.Db.StringSetAsync(kit.N("k"), "n:v0");
            }
        };

        (await kit.Client.TryAddAsync("k", new Item("v1"), null, Ct)).Kind.Should().Be(TryAddKind.Exists);

        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v0");
    }

    private static string ReverseFence(KeyMoveKit kit, string key) => kit.Names.ReverseFence(kit.L(key)).ToString();

    private Task<KeyMoveKit> CreateAsync(bool scripts, bool noOrigin = false) =>
        KeyMoveKit.CreateAsync(fixture, scripts, noOrigin, inPlace: new TaggedItemCodec("l:"), native: new TaggedItemCodec("n:"));
}
