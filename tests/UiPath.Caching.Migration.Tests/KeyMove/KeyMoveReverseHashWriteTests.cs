using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The hash writes, field writes and refreshes of the reverse states.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveReverseHashWriteTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S1_rev_replace_writes_N_with_a_version_then_deletes_L_under_the_reverse_fence(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "before");

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(DateTimeOffset.UtcNow.AddMinutes(10)), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceSet, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.AllFieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("x", "_version_");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Db.StringGetAsync(ReverseFence(kit, "k"))).ToString().Should().NotBe("before");
        (await kit.Kit.FenceAsync("k")).Should().BeNull("a reverse write never replaces the forward fence");
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    [InlineData("S4", false)]
    public async Task S3_and_S4_rev_replace_deletes_N_then_writes_L_in_its_own_format(string state, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "before");

        var result = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(DateTimeOffset.UtcNow.AddMinutes(10)), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.HashWrite);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" }, "the in-place side keeps no system field");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        var fence = ReverseFence(kit, "k");
        (await kit.Kit.ExistsAsync(fence)).Should().BeTrue();
        (await kit.Db.StringGetAsync(fence)).ToString().Should().NotBeNullOrEmpty().And.NotBe("before");
        (await kit.Kit.FenceAsync("k")).Should().BeNull();
    }

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S3", true)]
    [InlineData("S4", false)]
    public async Task A_reverse_key_delete_deletes_N_then_L_under_the_reverse_fence(string state, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
    }

    [Fact]
    public async Task A_replace_with_no_fields_in_a_reverse_state_is_a_delete_of_the_key()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("fields")]
    public async Task S1_rev_refuses_a_field_write_as_the_reverse_barrier_and_stores_nothing(string how)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);

        var result = how == "merge"
            ? await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)
            : await kit.Client.RemoveFieldsAsync("k", ["a"], Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        meter.Count("move_merge_blocked", "reason", "reverse_barrier").Should().Be(1);
        kit.Kit.Schedule.Steps.Should().BeEmpty();
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("S1", "replace")]
    [InlineData("S1", "delete")]
    [InlineData("S1", "merge")]
    [InlineData("S1", "fields")]
    [InlineData("S3", "replace")]
    [InlineData("S3", "replace with metadata")]
    [InlineData("S3", "delete")]
    [InlineData("S3", "merge")]
    [InlineData("S3", "fields")]
    [InlineData("S4", "replace")]
    [InlineData("S4", "replace with metadata")]
    [InlineData("S4", "delete")]
    [InlineData("S4", "merge")]
    [InlineData("S4", "fields")]
    public async Task Reverse_states_are_closed_for_hash_writes_of_a_NoOrigin_cache(string state, string how)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.N("k"), Original);

        var write = how switch
        {
            "replace" => async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct),
            "replace with metadata" => async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, new Dictionary<string, string?> { ["owner"] = "me" }), Ct),
            "delete" => async () => await kit.Client.RemoveAsync("k", Ct),
            "merge" => async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct),
            _ => (Func<Task<WriteResult>>)(async () => await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)),
        };

        await write.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        kit.Kit.Schedule.Steps.Should().BeEmpty();
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task Metadata_is_refused_where_a_reverse_write_lands_on_an_in_place_format_that_cannot_hold_it(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };

        var refused = async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), Ct);

        (await refused.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(kit.Kit.Cid);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original, "a refused write changes nothing");
    }

    [Fact]
    public async Task S1_rev_keeps_metadata_because_the_write_lands_on_N()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", direction: "rev");
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.AllFieldsAsync(kit.N("k"))).Should().ContainKey("_metadata_");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task A_failed_reverse_hash_write_is_retried_under_the_reverse_fence_it_observed(bool scripts, bool fenceMoved)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "token-before");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("k").Should().BeTrue();

        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;
        if (fenceMoved)
        {
            await kit.SetAsync(kit.L("k"), Original);
            await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "newer");
        }

        await kit.Client.Retries.RunDueAsync(all: true);

        kit.Client.Retries.Count.Should().Be(0);
        var expected = fenceMoved ? Original : new Dictionary<string, string> { ["x"] = "9" };
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(expected);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        meter.Count("move_retry_superseded").Should().Be(fenceMoved ? 1 : 0);
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S3")]
    public async Task A_failed_reverse_key_delete_is_retried_and_a_moved_fence_supersedes_it(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.L("m"), Original);
        await FailAtBumpAsync(kit, "k");
        await kit.Client.Retries.RunDueAsync(all: true);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();

        await FailAtBumpAsync(kit, "m");
        await kit.Db.StringSetAsync(ReverseFence(kit, "m"), "newer");
        await kit.Client.Retries.RunDueAsync(all: true);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.FieldsAsync(kit.L("m"))).Should().BeEquivalentTo(Original, "a newer writer moved the fence");
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    public async Task A_reverse_merge_where_N_holds_no_hash_merges_into_L_under_the_reverse_fence(string state, bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "before");

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.AllFieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string>(Original) { ["d"] = "4" });
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Db.StringGetAsync(ReverseFence(kit, "k"))).ToString().Should().NotBe("before");
    }

    [Theory]
    [InlineData("S4", "reverse_residue", "2")]
    [InlineData("S4", "reverse_residue", "junk:x")]
    public async Task A_reverse_merge_while_N_holds_the_hash_is_blocked_and_changes_nothing(string state, string reason, string value)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        var held = new Dictionary<string, string> { ["a"] = "1", ["b"] = value };
        await kit.SetAsync(kit.N("k"), held);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        meter.Count("move_merge_blocked", "reason", reason).Should().Be(1);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("a partial L would hide the fields of N");
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(held);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S3_rev_field_delete_deletes_the_field_of_N_then_of_L_under_the_reverse_fence(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3", direction: "rev");
        await kit.SetAsync(kit.N("k"), Original);
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "before");

        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.HashDelete);
        (await kit.FieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("b", "c");
        (await kit.FieldsAsync(kit.L("k"))).Keys.Should().BeEquivalentTo("b", "c");
        (await kit.Db.StringGetAsync(ReverseFence(kit, "k"))).ToString().Should().NotBe("before");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S4_rev_field_delete_deletes_the_field_of_L_and_of_N_only_when_N_holds_the_hash(bool residue)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S4", direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);
        if (residue)
        {
            await kit.SetAsync(kit.N("k"), Original);
        }

        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.L("k"))).Keys.Should().BeEquivalentTo("b", "c");
        if (residue)
        {
            kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.ProbeSource, SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.HashDelete);
            (await kit.FieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("b", "c");
        }
        else
        {
            kit.Kit.Schedule.Steps.Should().Equal(SchedulePointStep.ProbeSource, SchedulePointStep.HashDelete);
        }
    }

    [Theory]
    [InlineData("S1", "N")]
    [InlineData("S1", "L")]
    [InlineData("S3", "L")]
    [InlineData("S4", "L")]
    [InlineData("S4", "N")]
    public async Task A_reverse_refresh_extends_the_side_that_holds_the_hash(string state, string side)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        var holder = side == "N" ? kit.N("k") : kit.L("k");
        await kit.SetAsync(holder, Original, TimeSpan.FromMinutes(1));

        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(30)), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Db.KeyTimeToLiveAsync(holder))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(29));
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task A_reverse_refresh_of_a_key_that_neither_side_holds_is_not_sent(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");

        (await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(30)), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S3")]
    public async Task A_reverse_refresh_that_carries_metadata_is_refused_without_changing_the_expiry(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));
        var expiry = await kit.Db.KeyExpireTimeAsync(kit.L("k"));

        var refresh = async () => await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(30)), new Dictionary<string, string?> { ["owner"] = "me" }, Ct);

        var refused = await refresh.Should().ThrowAsync<NotSupportedException>();
        (await kit.Db.KeyExpireTimeAsync(kit.L("k"))).Should().Be(expiry);
        refused.Which.Message.Should().Contain($"the metadata field has no checked form for a refresh in state Reverse{state}");
    }

    [Theory]
    [InlineData("S1", false)]
    [InlineData("S1", true)]
    [InlineData("S3", false)]
    [InlineData("S3", true)]
    [InlineData("S4", false)]
    [InlineData("S4", true)]
    public async Task A_NoOrigin_refresh_is_closed_in_every_reverse_state(string state, bool withMetadata)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: "rev");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(1));
        var expiry = await kit.Db.KeyExpireTimeAsync(kit.L("k"));

        var requestedExpiry = CacheExpiry.After(TimeSpan.FromMinutes(30));
        var refresh = async () => withMetadata
            ? await kit.Client.RefreshAsync("k", requestedExpiry, new Dictionary<string, string?> { ["owner"] = "me" }, Ct)
            : await kit.Client.RefreshAsync("k", requestedExpiry, Ct);

        await refresh.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.Db.KeyExpireTimeAsync(kit.L("k"))).Should().Be(expiry);
    }

    [Theory]
    [InlineData("S1", "rev", typeof(CacheUnavailableException))]
    [InlineData("S3", "rev", typeof(CacheUnavailableException))]
    [InlineData("S4", "rev", typeof(CacheUnavailableException))]
    [InlineData("S1", "fwd", typeof(NotSupportedException))]
    [InlineData("S2", "fwd", typeof(NotSupportedException))]
    public async Task A_fill_with_metadata_is_refused_in_a_closed_reverse_cache_or_a_forward_format_without_metadata(string state, string direction, Type exceptionType)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: direction == "rev");
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: direction);
        var options = new HashWriteOptions(HashCacheSetOption.KeyReplace, null, new Dictionary<string, string?> { ["owner"] = "me" });

        var fill = async () => await kit.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), options, null, Ct);

        (await fill.Should().ThrowAsync<Exception>()).Which.Should().BeOfType(exceptionType);
    }

    // The bump of a key delete is lost: the call is unknown and a retry waits for it, over the fence in force before it.
    private static async Task FailAtBumpAsync(KeyMoveHashKit kit, string key)
    {
        await kit.Db.StringSetAsync(ReverseFence(kit, key), "token-before");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        (await kit.Client.RemoveAsync(key, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;
        kit.Client.Retries.IsPending(key).Should().BeTrue();
    }

    private static string ReverseFence(KeyMoveHashKit kit, string key) => kit.Kit.Names.ReverseFence(kit.L(key)).ToString();

    private static HashWriteOptions Replace(DateTimeOffset? deadline = null) => new(HashCacheSetOption.KeyReplace, deadline);

    private static HashWriteOptions Merge() => new(HashCacheSetOption.HashReplace, null);
}
