using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Refresh of a hash: a hash is refreshed where it lives, by a checked script, in each forward state.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashRefreshTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    public async Task In_S1_and_S2_a_refresh_extends_L_and_a_hash_that_is_not_there_is_not_refreshed(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "none");
        await kit.Kit.SetStateAsync(state, nPop: 0);
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RefreshAsync("none", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
        (await kit.Kit.FenceAsync("k")).Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_S3_a_refresh_extends_N_when_it_holds_the_key_and_replaces_the_fence(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
        var before = await kit.Kit.FenceAsync("k");

        (await kit.Client.RefreshAsync("k", CacheExpiry.At(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromHours(3))), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(2));
        (await kit.Kit.FenceAsync("k")).Should().NotBe(before);
        kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
    }

    [Fact]
    public async Task In_S3_a_hash_only_L_holds_is_promoted_and_then_refreshed_in_N()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
    }

    // A refresh is acknowledged only where it lands on N. A hash that can only be refreshed in L is not sent and L is left as it is, because a field delete can make it promotable and a promoter would publish the earlier expiry.
    [Theory]
    [InlineData("too large", true)]
    [InlineData("too large", false)]
    [InlineData("unreadable", true)]
    [InlineData("unreadable", false)]
    public async Task In_S3_a_hash_that_cannot_be_promoted_is_not_sent_and_L_is_left_untouched(string reason, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true, maxFields: 2);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var stuck = reason == "too large"
            ? new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" }
            : new Dictionary<string, string> { ["a"] = "junk:1", ["b"] = "2" };
        await kit.SetAsync(kit.L("k"), stuck, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(stuck);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    // The field delete of S3 makes a hash that could not be promoted promotable: whatever the refresh answered, an extension it acknowledged must be on the hash that the promoter publishes.
    [Theory]
    [InlineData("too large", true)]
    [InlineData("too large", false)]
    [InlineData("unreadable", true)]
    [InlineData("unreadable", false)]
    public async Task A_field_delete_that_makes_a_stuck_hash_promotable_never_loses_an_acknowledged_refresh(string reason, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true, maxFields: 2);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var stuck = reason == "too large"
            ? new Dictionary<string, string> { ["a"] = "1", ["b"] = "2", ["c"] = "3" }
            : new Dictionary<string, string> { ["a"] = "junk:1", ["b"] = "2" };
        await kit.SetAsync(kit.L("k"), stuck, TimeSpan.FromMinutes(1));

        var refreshed = await kit.Client.RefreshAsync("k", Hours(3), Ct);
        (await kit.Client.RemoveFieldsAsync("k", [reason == "too large" ? "c" : "a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        var promoted = await kit.Client.ReadAsync("k", null, Ct);
        var left = await kit.Db.KeyTimeToLiveAsync(kit.N("k"));

        promoted.Kind.Should().Be(ReadOutcome.Found, "the hash is promotable now");
        left.Should().NotBeNull();
        if (refreshed.Outcome == WriteOutcome.Committed)
        {
            left.Value.Should().BeGreaterThan(TimeSpan.FromHours(2), "an acknowledged refresh is on the hash that stays");
        }

        refreshed.Outcome.Should().Be(WriteOutcome.NotSent);
    }

    // The promotions of the first two tries are refused (the fence moves), and only the third succeeds: the refresh asks N again after it, so the extension still lands.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refresh_whose_third_promotion_is_the_first_to_succeed_still_extends_N(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true, options: new KeyMoveOptions { MaxFenceRetries = 0 });
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));
        var fence = kit.Kit.Names.ForwardFence(kit.N("k"));
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth <= 2)
            {
                await kit.Db.StringSetAsync(fence, Guid.NewGuid().ToString("N"));
            }
        };

        var refreshed = await kit.Client.RefreshAsync("k", Hours(3), Ct);

        kit.Kit.Schedule.Steps.Count(s => s == SchedulePointStep.Publish).Should().Be(3);
        refreshed.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(2));
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    // The refresh misses N, another promoter then publishes N from L and has not yet deleted L. Extending only L would be lost when that promoter's compare deletes L, so the refresh asks N again and extends the copy that stays.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refresh_that_misses_N_and_meets_a_promoter_that_published_N_extends_N_and_not_only_L(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));
        kit.Kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.ProbeSource && nth == 1)
            {
                // The other promoter's publish, with the life it read from L; its delete of L has not run.
                await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
            }
        };

        var refreshed = await kit.Client.RefreshAsync("k", Hours(3), Ct);
        kit.Kit.Schedule.Hook = null;
        await kit.Db.KeyDeleteAsync(kit.L("k"));

        refreshed.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(2), "the acknowledged life is on the copy that survives");
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
    }

    // A promotion that keeps being refused leaves the hash where it is: the refresh is not acknowledged, and L is not extended with a deadline that a later publication could drop.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refresh_whose_promotion_never_finishes_is_not_sent_and_does_not_extend_L(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));
        var fence = kit.Kit.Names.ForwardFence(kit.N("k"));
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.Publish)
            {
                await kit.Db.StringSetAsync(fence, Guid.NewGuid().ToString("N"));
            }
        };

        var refreshed = await kit.Client.RefreshAsync("k", Hours(3), Ct);

        refreshed.Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(KeyMoveStates.Ready, "ready")]
    [InlineData(KeyMoveStates.Barrier, "barrier")]
    [InlineData(KeyMoveStates.Moving, "moving")]
    [InlineData(KeyMoveStates.Drained, "drained")]
    public async Task A_refresh_that_carries_metadata_is_refused_before_the_keys_have_moved_and_one_without_it_works(string state, string stageName)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: state is KeyMoveStates.Ready or KeyMoveStates.Barrier ? 0 : 1);
        await kit.SetAsync(state is KeyMoveStates.Ready or KeyMoveStates.Barrier ? kit.L("k") : kit.N("k"), Original, TimeSpan.FromMinutes(1));

        var emptyMetadata = new Dictionary<string, string?>();
        var ownerMetadata = new Dictionary<string, string?> { ["owner"] = "me" };
        foreach (var metadata in new[] { emptyMetadata, ownerMetadata })
        {
            var refused = async () => await kit.Client.RefreshAsync("k", Hours(2), metadata, Ct);
            (await refused.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(kit.Kit.Cid).And.Contain($"state {stageName}.");
        }

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
    }

    // An expiry that has passed deletes the key, so a metadata field written after it would make a key of its own that never expires: the metadata is changed first, and nothing is left behind.
    [Theory]
    [InlineData(true, "Keep", true)]
    [InlineData(true, "Remove", true)]
    [InlineData(true, "Mark", true)]
    [InlineData(true, "Keep", false)]
    [InlineData(true, "Remove", false)]
    [InlineData(true, "Mark", false)]
    [InlineData(false, "Keep", true)]
    [InlineData(false, "Remove", true)]
    [InlineData(false, "Mark", true)]
    [InlineData(false, "Keep", false)]
    [InlineData(false, "Remove", false)]
    [InlineData(false, "Mark", false)]
    public async Task A_refresh_script_or_transaction_whose_deadline_has_passed_leaves_no_key_and_no_marker_whatever_it_does_to_the_metadata(bool scripts, string mode, bool present)
    {
        var clear = Enum.Parse<HashMetadataClear>(mode);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        if (present)
        {
            await kit.Db.HashSetAsync(kit.N("k"), [new HashEntry("a", "1"), new HashEntry("_metadata_", "{\"owner\":\"me\"}")]);
            await kit.Db.KeyExpireAsync(kit.N("k"), TimeSpan.FromMinutes(5));
        }

        var control = kit.Kit.Names.ControlFor(kit.Kit.Group, kit.N("k"));
        var past = kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromSeconds(-1)).ToUnixTimeMilliseconds();
        var fence = new FenceBump(kit.Kit.Names.ForwardFence(kit.N("k")), "nonce", 60_000);

        var code = await kit.Kit.Scripts.HashExpireAsync(kit.N("k"), control, kit.Kit.Id, past, fence, clear);

        code.Should().Be(present ? 1 : 0);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("the key was deleted by the expiry that had passed, and nothing recreated it");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k"))).Should().BeNull();
    }

    // The same with a deadline in the future: each of the three does what it says to the metadata, and the key keeps its life.
    [Theory]
    [InlineData(true, "Keep")]
    [InlineData(true, "Remove")]
    [InlineData(true, "Mark")]
    [InlineData(false, "Keep")]
    [InlineData(false, "Remove")]
    [InlineData(false, "Mark")]
    public async Task A_refresh_in_time_keeps_removes_or_marks_the_metadata_and_sets_the_life(bool scripts, string mode)
    {
        var clear = Enum.Parse<HashMetadataClear>(mode);
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.Db.HashSetAsync(kit.N("k"), [new HashEntry("a", "1"), new HashEntry("_metadata_", "{\"owner\":\"me\"}")]);
        await kit.Db.KeyExpireAsync(kit.N("k"), TimeSpan.FromMinutes(5));
        var control = kit.Kit.Names.ControlFor(kit.Kit.Group, kit.N("k"));

        var code = await kit.Kit.Scripts.HashExpireAsync(kit.N("k"), control, kit.Kit.Id, kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromHours(2)).ToUnixTimeMilliseconds(), new FenceBump(kit.Kit.Names.ForwardFence(kit.N("k")), "nonce", 60_000), clear);

        code.Should().Be(1);
        var fields = await kit.AllFieldsAsync(kit.N("k"));
        fields.Should().ContainKey("a");
        switch (clear)
        {
            case HashMetadataClear.Keep:
                fields["_metadata_"].Should().Contain("owner");
                break;
            case HashMetadataClear.Remove:
                fields.Should().NotContainKey("_metadata_");
                break;
            default:
                fields["_metadata_"].Should().BeEmpty();
                break;
        }

        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
    }

    // The deadline is already past on Redis when the command runs, while the client clock still sees it ahead.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task A_refresh_whose_deadline_passes_before_its_command_runs_leaves_no_persistent_hash_and_no_marker(bool scripts, bool cacheNulls)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, nativeNulls: cacheNulls);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(5));
        kit.Kit.Time.UtcOffset = TimeSpan.FromSeconds(-2);

        var beforeRefresh = kit.Kit.Clock.GetUtcNow();
        var refreshed = await kit.Client.RefreshAsync("k", CacheExpiry.At(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromSeconds(1))), (IDictionary<string, string?>?)null, Ct);

        kit.Kit.Clock.GetUtcNow().Should().Be(beforeRefresh);
        refreshed.Outcome.Should().Be(WriteOutcome.Committed, "the hash was there when the refresh ran");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("it expired, and nothing made a key of the marker");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k"))).Should().BeNull();
    }

    [Fact]
    public async Task In_S3_a_hash_that_is_nowhere_is_not_refreshed()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public async Task In_S4_a_refresh_extends_N_and_a_hash_only_L_holds_is_not_sent_and_L_is_left_untouched()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m");
        await kit.Kit.SetStateAsync("S4");
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));
        await kit.SetAsync(kit.L("m"), Original, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RefreshAsync("m", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
        (await kit.Db.KeyTimeToLiveAsync(kit.L("m")))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        (await kit.Kit.ExistsAsync(kit.N("m"))).Should().BeFalse("S4 promotes nothing");
    }

    [Fact]
    public async Task In_S5_a_refresh_is_a_native_extension_of_N_with_no_fence()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S5", swept: true);
        await kit.SetAsync(kit.N("k"), Original, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", Hours(2), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromHours(1));
        (await kit.Kit.FenceAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task A_refresh_to_a_life_that_has_passed_is_a_delete_and_one_with_no_resolved_life_is_refused()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        await kit.SetAsync(kit.L("k"), Original);

        var unresolved = async () => await kit.Client.RefreshAsync("k", CacheExpiry.Default, Ct);
        await unresolved.Should().ThrowAsync<ArgumentException>();
        (await kit.Client.RefreshAsync("k", CacheExpiry.At(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromSeconds(-1))), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_reverse_state_with_no_write_form_closes_the_refresh()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S2", direction: "rev");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));

        var refresh = async () => await kit.Client.RefreshAsync("k", Hours(2), Ct);

        await refresh.Should().ThrowAsync<CacheUnavailableException>();
    }

    [Fact]
    public async Task The_expiry_and_the_presence_of_a_hash_are_read_from_the_side_that_holds_it_with_no_promotion()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m", "none");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(10));
        await kit.SetAsync(kit.N("m"), Original);

        (await kit.Client.ExpireTimeAsync("k", Ct)).Should().BeCloseTo(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), TimeSpan.FromSeconds(30));
        (await kit.Client.ContainsAsync("k", Ct)).Should().BeTrue();
        (await kit.Client.ExpireTimeAsync("m", Ct)).Should().BeNull("no expiry");
        (await kit.Client.ContainsAsync("m", Ct)).Should().BeTrue();
        (await kit.Client.ContainsAsync("none", Ct)).Should().BeFalse();

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeTrue("a read of the life promotes nothing");
        kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.Capture);
    }

    private static CacheExpiry Hours(int hours) => CacheExpiry.After(TimeSpan.FromHours(hours));
}
