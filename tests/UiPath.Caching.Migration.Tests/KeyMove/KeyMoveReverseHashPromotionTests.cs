using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The reverse promotion of a hash (read, merge, refresh) and the reverse hash fills.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveReverseHashPromotionTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_read_serves_the_hash_of_N_and_promotes_its_user_fields_to_L(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string>(Original) { ["_version_"] = "7" }, TimeSpan.FromMinutes(10));

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Keys.Should().BeEquivalentTo("a", "b", "c");
        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original, "the in-place side keeps the user fields only");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
        (await kit.Kit.FenceAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task An_S3_rev_promotion_replaces_an_older_hash_in_L_and_drops_metadata_the_in_place_format_cannot_hold()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["x"] = "9", ["_metadata_"] = "{\"owner\":\"me\"}" });
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Keys.Should().BeEquivalentTo("x");

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task An_S3_rev_promotion_keeps_metadata_when_the_in_place_format_carries_it()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: new ItemHashCodec(systemFields: true));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        var stored = new Dictionary<string, string>(Original) { ["_metadata_"] = "{\"owner\":\"me\"}" };
        await kit.SetAsync(kit.N("k"), stored);

        _ = await kit.Client.ReadAsync("k", null, Ct);

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(stored);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_promotion_keeps_a_hash_without_an_expiry_persistent(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);

        _ = await kit.Client.ReadAsync("k", null, Ct);

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k"))).Should().BeNull();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task An_S3_rev_read_of_a_NoOrigin_hash_serves_N_without_promoting()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);

        var read = await kit.Client.ReadAsync("k", null, Ct);

        read.Fields!.Keys.Should().BeEquivalentTo("a", "b", "c");
        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S4")]
    public async Task A_reverse_hash_read_outside_S3_rev_promotes_nothing(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);

        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Count.Should().Be(3);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task An_S3_rev_hash_read_that_misses_N_reads_L_and_sets_no_hint()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Count.Should().Be(3);

        kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.ReadDestinationOnMaster, SchedulePointStep.ReadSource);
        kit.Client.Hints.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reverse_read_recovers_selected_fields_from_the_answering_hash(bool native)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        var side = native ? kit.N("k") : kit.L("k");
        var stored = new Dictionary<string, string>(Original) { ["broken"] = "junk:value" };
        await kit.SetAsync(side, stored);

        var read = await kit.Client.ReadAsync("k", ["a", "broken"], Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Fields.Should().BeEquivalentTo(KeyMoveHashKit.Items(("a", "1")));
        read.Uncached.Should().BeTrue();
        SchedulePointStep[] steps = native ? [SchedulePointStep.ReadDestinationOnMaster] : [SchedulePointStep.ReadDestinationOnMaster, SchedulePointStep.ReadSource];
        kit.Kit.Schedule.Steps.Should().Equal(steps);
        (await kit.AllFieldsAsync(side)).Should().BeEquivalentTo(stored);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_merge_promotes_the_hash_of_N_first_and_then_merges_into_L(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string>(Original) { ["d"] = "4" });
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("too_large")]
    [InlineData("unreadable")]
    public async Task An_S3_rev_merge_is_blocked_when_the_hash_of_N_cannot_be_promoted(string reason)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, maxFields: 2);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        var held = reason == "too_large" ? Original : new Dictionary<string, string> { ["a"] = "junk:x" };
        await kit.SetAsync(kit.N("k"), held);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        meter.Count("move_merge_blocked", "reason", reason).Should().Be(1);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(held, "N is the authority and is never dropped in reverse");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("_metadata_")]
    [InlineData("a")]
    public async Task An_S3_rev_merge_blocks_when_N_has_no_storable_fields_and_L_is_stale(string emptyField)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, nativeNulls: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        var native = new Dictionary<string, string> { [emptyField] = string.Empty };
        var stale = new Dictionary<string, string> { ["stale"] = "1" };
        await kit.SetAsync(kit.N("k"), native);
        await kit.SetAsync(kit.L("k"), stale);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(native);
        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(stale);
    }

    [Fact]
    public async Task An_S3_rev_refresh_promotes_the_hash_of_N_and_extends_L()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(30)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(29));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_S3_rev_refresh_leaves_the_expiry_of_a_hash_that_cannot_be_promoted(bool unreadable)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, maxFields: 2);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        var held = unreadable ? new Dictionary<string, string> { ["a"] = "junk:x" } : Original;
        await kit.SetAsync(kit.N("k"), held, TimeSpan.FromMinutes(1));
        var expiry = await kit.Db.KeyExpireTimeAsync(kit.N("k"));

        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(30)), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Db.KeyExpireTimeAsync(kit.N("k"))).Should().Be(expiry);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_S3_rev_refresh_keeps_its_expiry_when_another_read_runs_during_promotion(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
        var deadline = DateTimeOffset.FromUnixTimeSeconds(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(30)).ToUnixTimeSeconds());
        var refresh = WriteOutcome.NotSent;
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.ConfirmDestination && nth == 1)
            {
                refresh = (await kit.Client.RefreshAsync("k", CacheExpiry.At(deadline), Ct)).Outcome;
                _ = await kit.Client.ReadAsync("k", null, Ct);
            }
        };

        _ = await kit.Client.ReadAsync("k", null, Ct);

        refresh.Should().Be(WriteOutcome.Committed);
        (await kit.Db.KeyExpireTimeAsync(kit.L("k"))).Should().Be(deadline.UtcDateTime);
        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    // The direction leaves the forward run as it was: L to N under G, then the delete of L.
    [Fact]
    public async Task A_forward_hash_promotion_is_unchanged_by_the_reverse_direction()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Count.Should().Be(3);

        kit.Kit.Schedule.Steps.Should().Equal(
            SchedulePointStep.ReadDestination,
            SchedulePointStep.ReadDestinationOnMaster,
            SchedulePointStep.ProbeSource,
            SchedulePointStep.Capture,
            SchedulePointStep.ReadSource,
            SchedulePointStep.StageHash,
            SchedulePointStep.Publish,
            SchedulePointStep.ConfirmDestination,
            SchedulePointStep.CompareDelete);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.FenceAsync("k")).Should().NotBeNull();
        (await kit.Kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_forward_hash_promotion_still_publishes_only_if_N_is_absent()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["z"] = "9" });
            }
        };

        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Keys.Should().BeEquivalentTo("z");

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["z"] = "9" });
    }

    [Fact]
    public async Task An_S1_rev_hash_fill_creates_N_under_the_forward_fence()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", direction: "rev");

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Replace(), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.FenceAsync("k")).Should().NotBeNull();
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task An_S3_and_S4_rev_hash_fill_creates_L_in_its_own_format_under_the_reverse_fence(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Replace(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10))), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S3", false)]
    public async Task A_reverse_hash_fill_that_loses_returns_the_stored_hash(string state, bool onN)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(onN ? kit.N("k") : kit.L("k"), Original);
        var box = new HashFillBox<Item>();

        (await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "9")), Replace(), box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        box.Lost.Should().BeTrue();
        box.Stored.Should().BeEquivalentTo(KeyMoveHashKit.Items(("a", "1"), ("b", "2"), ("c", "3")));
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task Reverse_states_close_a_hash_fill_of_a_NoOrigin_cache(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");

        var fill = async () => await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Replace(), null, Ct);

        await fill.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    private static string ReverseFence(KeyMoveHashKit kit, string key) => kit.Kit.Names.ReverseFence(kit.L(key)).ToString();

    private static HashWriteOptions Replace(DateTimeOffset? deadline = null) => new(HashCacheSetOption.KeyReplace, deadline);

    private static HashWriteOptions Merge() => new(HashCacheSetOption.HashReplace, null);
}
