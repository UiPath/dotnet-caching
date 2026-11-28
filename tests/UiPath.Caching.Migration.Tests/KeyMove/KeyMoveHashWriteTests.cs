using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Forward writes of a hash: the whole replace, the field merge, the field delete, the key delete in each state, with their order, on the real scripts and on the transaction fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashWriteTests(RedisFixture fixture)
{
    private static readonly Dictionary<string, string> Original = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_empty_replacement_still_deletes_the_source_when_its_deadline_passes_after_conversion()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("key");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("key"), Original);
        var deadline = kit.Kit.Time.GetUtcNow().AddMinutes(1);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                kit.Kit.Time.Advance(TimeSpan.FromMinutes(2));
            }

            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("key", KeyMoveHashKit.Items(), Replace(deadline), Ct);

        (await kit.Kit.ExistsAsync(kit.L("key"))).Should().BeFalse();
        result.Outcome.Should().Be(WriteOutcome.Committed);
    }

    [Fact]
    public async Task An_expired_merge_after_the_source_delete_registers_a_pending_write()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("key");
        await kit.Kit.SetStateAsync("S5", swept: false);
        await kit.SetAsync(kit.L("key"), Original);
        await kit.SetAsync(kit.N("key"), Original);
        var deadline = kit.Kit.Time.GetUtcNow().AddMinutes(1);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.NativeWrite)
            {
                kit.Kit.Time.Advance(TimeSpan.FromMinutes(2));
            }

            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("key", KeyMoveHashKit.Items(("late", "4")), Merge(deadline), Ct);

        (await kit.Kit.ExistsAsync(kit.L("key"))).Should().BeFalse();
        (await kit.FieldsAsync(kit.N("key"))).Should().BeEquivalentTo(Original);
        result.Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("key").Should().BeTrue();
    }

    [Fact]
    public async Task A_late_marked_hash_write_is_refused_and_counted()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { CommandTimeout = TimeSpan.FromMinutes(-1) });
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("key");
        await kit.Kit.SetStateAsync("S3");

        var result = await kit.Client.SetAsync("key", KeyMoveHashKit.Items(("value", "1")), Replace(), Ct);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        meter.Count("cache.keymove.marker_late").Should().Be(1);
        (await kit.Kit.ExistsAsync(kit.N("key"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_managed_merge_refuses_an_elapsed_deadline_without_deleting_the_hash(bool scripts, bool expiresWhileQueued)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts, clock: clock);
        await kit.Kit.PrepareAsync("key");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        await kit.SetAsync(kit.L("key"), Original);
        var deadline = clock.GetUtcNow().AddMinutes(1);
        if (expiresWhileQueued)
        {
            kit.Kit.Schedule.Hook = (step, _) =>
            {
                if (step == SchedulePointStep.SourceSet)
                {
                    clock.Advance(TimeSpan.FromMinutes(2));
                }

                return Task.CompletedTask;
            };
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        var options = new HashWriteOptions(HashCacheSetOption.HashReplace, deadline);
        (await kit.Client.SetAsync("key", KeyMoveHashKit.Items(("late", "4")), options, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.FieldsAsync(kit.L("key"))).Should().BeEquivalentTo(Original);
    }

    // A key with no retry answers the one sentinel the string path uses, from the key-move client and from a client that keeps no retries.
    [Fact]
    public async Task A_key_with_no_retry_answers_the_shared_no_retry_sentinel()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);

        kit.Client.SettledAsync("k").Should().BeSameAs(PathClientTasks.NoPendingRetry);
        ((IHashPathClient<Item>)kit.Plain).SettledAsync("k").Should().BeSameAs(PathClientTasks.NoPendingRetry);
    }

    [Fact]
    public async Task In_S1_every_write_is_one_script_on_L_and_N_and_the_fence_are_never_touched()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S1", nPop: 0);
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(5));

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string>(Original) { ["d"] = "4" });
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k"))).Should().BeNull("a merge with no expiry drops the earlier one, as the library's does");

        (await kit.Client.RemoveFieldsAsync("k", ["a", "b"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["c"] = "3", ["d"] = "4" });

        var until = kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10));
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(until), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        (await kit.Db.KeyExpireTimeAsync(kit.L("k"))).Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(until.ToUnixTimeMilliseconds()).UtcDateTime);
        (await kit.AllFieldsAsync(kit.L("k"))).Should().NotContainKey("_version_", "the in-place side keeps no system field");

        (await kit.Client.RemoveFieldsAsync("k", ["x"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("a delete of the last field removes the key");

        await kit.SetAsync(kit.L("k"), Original);
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.FenceAsync("k")).Should().BeNull();
        kit.Plain.Sets.Should().Be(0);
    }

    [Fact]
    public async Task In_S2_a_cache_with_an_origin_writes_like_S1_and_a_NoOrigin_cache_is_closed_for_every_write()
    {
        await using var origin = await KeyMoveHashKit.CreateAsync(fixture);
        await origin.Kit.PrepareAsync("k");
        await origin.Kit.SetStateAsync("S2", nPop: 0);
        (await origin.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await origin.FieldsAsync(origin.L("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "1" });

        await using var closed = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await closed.Kit.PrepareAsync("k");
        await closed.Kit.SetStateAsync("S2", nPop: 0);

        foreach (var write in new Func<Task>[]
        {
            async () => await closed.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), Replace(), Ct),
            async () => await closed.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), Merge(), Ct),
            async () => await closed.Client.RemoveAsync("k", Ct),
            async () => await closed.Client.RemoveFieldsAsync("k", ["a"], Ct),
            async () => await closed.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), Ct),
            async () => await closed.Client.FillAsync("k", KeyMoveHashKit.Items(("a", "1")), Replace(), null, Ct),
        })
        {
            await write.Should().ThrowAsync<CacheUnavailableException>();
        }

        (await closed.Kit.ExistsAsync(closed.L("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_S3_a_replace_deletes_L_first_and_then_writes_N_with_a_version_and_a_new_fence(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        var before = await kit.Kit.FenceAsync("k");
        bool? lAtDestination = null;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                lAtDestination = await kit.Kit.ExistsAsync(kit.L("k"));
            }
        };

        var deadline = kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(5));
        var written = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(deadline), Ct);

        written.Outcome.Should().Be(WriteOutcome.Committed);
        lAtDestination.Should().BeFalse("the source goes first, so a crash between the steps leaves a miss and never an older hash");
        kit.Kit.Schedule.Steps.Where(s => s is SchedulePointStep.SourceDelete or SchedulePointStep.HashWrite).Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.HashWrite);
        var stored = await kit.AllFieldsAsync(kit.N("k"));
        stored.Keys.Should().BeEquivalentTo("x", "_version_");
        (await kit.Kit.FenceAsync("k")).Should().NotBe(before);
        (await kit.Db.KeyExpireTimeAsync(kit.N("k"))).Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(deadline.ToUnixTimeMilliseconds()).UtcDateTime);
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("S4")]
    public async Task A_NoOrigin_replace_writes_N_with_its_marker_first_then_deletes_L_and_the_queue_removes_the_marker(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state);
        await kit.SetAsync(kit.L("k"), Original);
        string? marker = null;
        bool? nThere = null;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                marker = await kit.Kit.MarkerAsync("k");
                nThere = await kit.Kit.ExistsAsync(kit.N("k"));
            }
        };

        var written = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);

        written.Outcome.Should().Be(WriteOutcome.Committed);
        nThere.Should().BeTrue("a stored key never reads as a miss between the steps");
        marker.Should().NotBeNull("the marker is written with N, in the same script");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        kit.Client.Unmarks.Count.Should().Be(1);
        await kit.Client.Unmarks.RunDueAsync(all: true);
        (await kit.Kit.MarkerAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task A_NoOrigin_replace_whose_key_is_too_long_for_a_marker_fails_closed_before_any_step()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: true, options: new KeyMoveOptions { MarkerKeyMax = 5 });
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_S3_a_delete_removes_L_then_N_and_a_NoOrigin_delete_removes_the_marker_with_it(bool noOrigin)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, noOrigin: noOrigin);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["z"] = "0" });
        if (noOrigin)
        {
            await kit.Db.HashSetAsync(kit.Kit.Control("k"), kit.Kit.Field("k"), $"{kit.Kit.Id}|n0");
        }

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.Kit.MarkerAsync("k")).Should().BeNull();
        kit.Kit.Schedule.Steps.Where(s => s is SchedulePointStep.SourceDelete or SchedulePointStep.Bump).Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.Bump);
    }

    // The merge never runs ahead of the promotion, so a partial N never hides the fields of L that the merge did not name.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_S3_a_merge_promotes_the_whole_hash_first_and_then_merges_on_N(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromMinutes(5));

        var deadline = kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(7));
        var merged = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4"), ("a", "11")), Merge(deadline), Ct);

        merged.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "11", ["b"] = "2", ["c"] = "3", ["d"] = "4" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Db.KeyExpireTimeAsync(kit.N("k"))).Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(deadline.ToUnixTimeMilliseconds()).UtcDateTime);
        kit.Kit.Schedule.Steps.Should().ContainInOrder(SchedulePointStep.ProbeSource, SchedulePointStep.Capture, SchedulePointStep.Publish, SchedulePointStep.CompareDelete, SchedulePointStep.HashWrite);
    }

    [Fact]
    public async Task In_S3_a_merge_into_a_key_that_L_does_not_hold_goes_straight_to_N_with_no_capture()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["z"] = "0" });

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["z"] = "0", ["d"] = "4" });
        kit.Kit.Schedule.Steps.Should().Contain(SchedulePointStep.ProbeSource).And.NotContain(SchedulePointStep.Capture);
    }

    [Fact]
    public async Task A_merge_that_lost_to_a_change_of_L_after_the_publish_fails_closed_as_changing_and_sends_nothing()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.CompareDelete)
            {
                await kit.Db.HashSetAsync(kit.L("k"), "late", "1");
            }
        };

        var merged = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct);

        merged.Outcome.Should().Be(WriteOutcome.NotSent);
        meter.Count("move_merge_blocked", "reason", "changing").Should().Be(1);
        (await kit.FieldsAsync(kit.N("k"))).Should().NotContainKey("d", "the merge is never written while L still holds the hash");
        (await kit.FieldsAsync(kit.L("k"))).Should().ContainKey("late");
    }

    [Fact]
    public async Task A_merge_into_a_hash_above_the_field_cap_fails_closed_as_too_large_and_a_replace_still_works()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, maxFields: 3);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var big = Enumerable.Range(0, 5).ToDictionary(i => $"f{i}", i => $"v{i}");
        await kit.SetAsync(kit.L("k"), big);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        meter.Count("move_merge_blocked", "reason", "too_large").Should().Be(1);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(big, "a merge that fails closed leaves the hash where it is, dropped or not");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["d"] = "4" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("a replace deletes the hash of L");
    }

    [Fact]
    public async Task A_merge_into_an_unreadable_hash_fails_closed_as_unreadable()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        var junk = new Dictionary<string, string> { ["a"] = "junk:1", ["b"] = "2" };
        await kit.SetAsync(kit.L("k"), junk);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        meter.Count("move_merge_blocked", "reason", "unreadable").Should().Be(1);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(junk);
    }

    // In S4 a hash that L still holds, promotable or not, makes a merge fail closed; a replace is never blocked.
    [Fact]
    public async Task In_S4_a_merge_fails_closed_while_L_holds_the_hash_and_works_when_it_does_not_and_a_replace_is_never_blocked()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        using var meter = new MeterCollector(kit.Kit.Cid);
        await kit.Kit.PrepareAsync("k", "m");
        await kit.Kit.SetStateAsync("S4");
        await kit.SetAsync(kit.L("k"), Original, TimeSpan.FromSeconds(30));
        await kit.SetAsync(kit.N("m"), new Dictionary<string, string> { ["z"] = "0" });

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        meter.Count("move_merge_blocked", "reason", "src_residue").Should().Be(1);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("a partial N would hide the fields of L");

        (await kit.Client.SetAsync("m", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("m"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["z"] = "0", ["d"] = "4" });

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
    }

    // The field of L is deleted first, then the field of N.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task In_S3_a_field_delete_removes_the_field_of_L_before_the_field_of_N(bool scripts)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, scripts);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        bool? lHasItAtN = null;
        bool? nHasItAtL = null;
        kit.Kit.Schedule.Hook = async (step, _) =>
        {
            if (step == SchedulePointStep.SourceDelete)
            {
                nHasItAtL = await kit.Db.HashExistsAsync(kit.N("k"), "a");
            }

            if (step == SchedulePointStep.HashDelete)
            {
                lHasItAtN = await kit.Db.HashExistsAsync(kit.L("k"), "a");
            }
        };

        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        lHasItAtN.Should().BeFalse("the field of L is already gone when N's goes");
        nHasItAtL.Should().BeTrue();
        (await kit.FieldsAsync(kit.L("k"))).Keys.Should().BeEquivalentTo("b");
        (await kit.AllFieldsAsync(kit.N("k"))).Keys.Should().Contain("b").And.NotContain("a");

        (await kit.Client.RemoveFieldsAsync("k", ["b"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the last field takes the key with it, on both sides");
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("a hash left with only its count is removed");
    }

    [Fact]
    public async Task In_S4_a_field_delete_goes_to_N_and_also_to_L_when_L_holds_the_hash_and_L_goes_first()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m");
        await kit.Kit.SetStateAsync("S4");
        await kit.SetAsync(kit.L("k"), new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        await kit.SetAsync(kit.N("k"), new Dictionary<string, string> { ["b"] = "2", ["c"] = "3" });
        await kit.SetAsync(kit.N("m"), new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        (await kit.Client.RemoveFieldsAsync("k", ["b"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        kit.Kit.Schedule.Steps.Where(s => s is SchedulePointStep.SourceDelete or SchedulePointStep.HashDelete).Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.HashDelete);
        kit.Kit.Schedule.Reset();
        (await kit.Client.RemoveFieldsAsync("m", ["b"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.L("k"))).Keys.Should().BeEquivalentTo("a");
        (await kit.FieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("c");
        (await kit.FieldsAsync(kit.N("m"))).Keys.Should().BeEquivalentTo("a");
        kit.Kit.Schedule.Steps.Should().NotContain(SchedulePointStep.SourceDelete, "L holds nothing of m");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_replace_applies_while_the_fence_is_the_one_it_saw_and_a_newer_write_of_the_node_replaces_it(bool newer)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.Db.StringSetAsync(kit.Kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("first", "1")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("the first step ran");
        if (newer)
        {
            // Another node writes the key in the meantime: its write replaces the fence.
            (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("second", "2")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        }

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(newer ? new Dictionary<string, string> { ["second"] = "2" } : new Dictionary<string, string> { ["first"] = "1" });
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_retried_replace_that_meets_a_newer_write_is_dropped_and_counted()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashWrite)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("first", "1")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;

        // A write of another node, whose script replaces the fence.
        var other = new FencedKeys(kit.Kit.Names.ForwardFence(kit.N("k")), kit.N("k"), kit.Kit.Names.ControlFor(kit.Kit.Group, kit.N("k")));
        (await kit.Kit.Scripts.HashWriteAsync(kit.N("k"), other.Control, kit.Kit.Id, new ControlledHashWrite([new("other", "node")], HashCacheSetOption.KeyReplace, -1), new FenceBump(other.Fence, "other-node-nonce", 60_000))).Should().BeGreaterThanOrEqualTo(0);
        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.AllFieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["other"] = "node" });
        kit.Client.Retries.Count.Should().Be(0);
        meter.Count("move_retry_superseded").Should().Be(1);
    }

    [Fact]
    public async Task A_retried_field_delete_is_dropped_when_a_newer_write_replaced_the_fence()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.N("k"), Original);
        using var meter = new MeterCollector(kit.Kit.Cid);
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashDelete)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;
        var control = kit.Kit.Names.ControlFor(kit.Kit.Group, kit.N("k"));
        (await kit.Kit.Scripts.HashWriteAsync(kit.N("k"), control, kit.Kit.Id, new ControlledHashWrite([new("a", "newer")], HashCacheSetOption.HashReplace, -1), new FenceBump(kit.Kit.Names.ForwardFence(kit.N("k")), "other-node-nonce", 60_000))).Should().BeGreaterThanOrEqualTo(0);

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.FieldsAsync(kit.N("k"))).Should().ContainKey("a", "the retry never deletes a field a newer write set");
        meter.Count("move_retry_superseded").Should().Be(1);
    }

    [Fact]
    public async Task A_retried_field_delete_applies_while_the_fence_is_still_the_token_seen_before_the_first_attempt()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);
        await kit.SetAsync(kit.N("k"), Original);
        await kit.Db.StringSetAsync(kit.Kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.HashDelete)
            {
                kit.Kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Kit.Schedule.Hook = null;
        kit.Kit.Connected = true;

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.FieldsAsync(kit.N("k"))).Keys.Should().BeEquivalentTo("b", "c");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_S5_a_write_deletes_L_until_the_sweep_has_ended_and_then_writes_N_with_no_fence(bool swept)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k", "m", "n", "o");
        await kit.Kit.SetStateAsync("S5", swept: swept);
        foreach (var key in new[] { "k", "m", "n", "o" })
        {
            await kit.SetAsync(kit.L(key), Original);
            await kit.SetAsync(kit.N(key), new Dictionary<string, string> { ["z"] = "0", ["a"] = "n" });
        }

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.SetAsync("m", KeyMoveHashKit.Items(("d", "4")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RemoveFieldsAsync("n", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.RemoveAsync("o", Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["x"] = "9" });
        (await kit.FieldsAsync(kit.N("m"))).Should().BeEquivalentTo(new Dictionary<string, string> { ["z"] = "0", ["a"] = "n", ["d"] = "4" });
        (await kit.FieldsAsync(kit.N("n"))).Keys.Should().BeEquivalentTo("z");
        (await kit.Kit.ExistsAsync(kit.N("o"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().Be(swept);
        (await kit.Kit.ExistsAsync(kit.L("m"))).Should().Be(swept, "a merge that left its copy would be served by a union reader after N expires");
        (await kit.FieldsAsync(kit.L("n"))).Keys.Should().BeEquivalentTo(swept ? ["a", "b", "c"] : ["b", "c"]);
        (await kit.Kit.ExistsAsync(kit.L("o"))).Should().Be(swept);
        (await kit.Kit.FenceAsync("k")).Should().BeNull("S5 has no promoter, so no fence is replaced");
    }

    [Fact]
    public async Task A_write_in_a_reverse_state_with_no_write_form_is_unavailable_and_its_read_is_the_union()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S2", direction: "rev");
        await kit.SetAsync(kit.L("k"), Original);

        var write = async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(), Ct);
        var fields = async () => await kit.Client.RemoveFieldsAsync("k", ["a"], Ct);
        var refresh = async () => await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(5)), Ct);

        await write.Should().ThrowAsync<CacheUnavailableException>();
        await fields.Should().ThrowAsync<CacheUnavailableException>();
        await refresh.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.Client.ReadAsync("k", null, Ct)).Fields!.Count.Should().Be(3);
        (await kit.FieldsAsync(kit.L("k"))).Should().BeEquivalentTo(Original);
    }

    [Theory]
    [InlineData(KeyMoveStates.Ready, "fwd", "ready")]
    [InlineData(KeyMoveStates.Barrier, "fwd", "barrier")]
    [InlineData(KeyMoveStates.Moving, "rev", "moving (reverse)")]
    [InlineData(KeyMoveStates.Drained, "rev", "drained (reverse)")]
    public async Task Metadata_is_refused_where_the_hash_is_kept_in_the_in_place_format_and_written_to_N_elsewhere(string state, string direction, string stageName)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, nativeNulls: true);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, direction: direction, nPop: state is KeyMoveStates.Ready or KeyMoveStates.Barrier ? 0 : 1);
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };

        var refused = async () => await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), Ct);
        var refresh = async () => await kit.Client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), metadata, Ct);
        (await refused.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(kit.Kit.Cid).And.Contain($"state {stageName}.");
        (await refresh.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(kit.Kit.Cid).And.Contain($"state {stageName}.");
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("a refused write stores nothing");

        await kit.Kit.SetStateAsync(KeyMoveStates.Moving);
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.AllFieldsAsync(kit.N("k"))).Should().ContainKey("_metadata_");
        (await kit.Client.ReadAsync("k", null, Ct)).Metadata.Should().ContainKey("owner");
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    public async Task A_write_with_metadata_is_stored_in_L_in_every_state_where_the_codec_of_L_carries_it(string state)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, inPlace: new ItemHashCodec(false, true));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: 0);
        var metadata = new Dictionary<string, string?> { ["owner"] = "me" };

        var written = await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("a", "1")), new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata), Ct);

        written.Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.AllFieldsAsync(kit.L("k"))).Keys.Should().BeEquivalentTo("a", "_metadata_");
        (await kit.Client.ReadAsync("k", null, Ct)).Metadata.Should().ContainKey("owner");
    }

    [Fact]
    public async Task A_replace_whose_deadline_has_passed_is_a_delete_and_a_merge_of_nothing_sends_nothing_and_an_empty_replace_is_a_delete()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync("S3");
        await kit.SetAsync(kit.L("k"), Original);

        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(("x", "9")), Replace(kit.Kit.Clock.ToDateTimeOffset(TimeSpan.FromSeconds(-1))), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        await kit.SetAsync(kit.N("k"), Original);
        kit.Kit.Schedule.Reset();
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.FieldsAsync(kit.N("k"))).Should().BeEquivalentTo(Original);
        (await kit.Client.SetAsync("k", KeyMoveHashKit.Items(), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Kit.ExistsAsync(kit.N("k"))).Should().BeFalse("nothing to store is a delete of the key");
    }

    // Live writes and deletes in S3, then the drain, with the union read of every key agreeing with what was written.
    [Fact]
    public async Task Live_writes_and_deletes_in_S3_leave_every_hash_readable_with_no_stale_field_and_stay_so_through_S4_and_S5()
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 8).Select(i => $"k{i}").ToArray();
        await kit.Kit.PrepareAsync(keys);
        await kit.Kit.SetStateAsync("S3");
        var model = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (key, i) in keys.Select((k, i) => (k, i)))
        {
            if (i % 4 != 3)
            {
                var fields = Enumerable.Range(0, 3 + i).ToDictionary(f => $"f{f}", f => $"{key}-{f}");
                await kit.SetAsync(kit.L(key), fields);
                model[key] = fields;
            }
        }

        var random = new Random(17);
        for (var step = 0; step < 60; step++)
        {
            var key = keys[random.Next(keys.Length)];
            switch (random.Next(5))
            {
                case 0:
                    var replaced = new Dictionary<string, string> { [$"r{step}"] = $"v{step}", [$"s{step}"] = "x" };
                    (await kit.Client.SetAsync(key, KeyMoveHashKit.Items([.. replaced.Select(e => (e.Key, (string?)e.Value))]), Replace(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                    model[key] = replaced;
                    break;
                case 1:
                    (await kit.Client.SetAsync(key, KeyMoveHashKit.Items(($"m{step}", $"v{step}")), Merge(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                    model[key] = new Dictionary<string, string>(model.GetValueOrDefault(key) ?? []) { [$"m{step}"] = $"v{step}" };
                    break;
                case 2:
                    var some = (model.GetValueOrDefault(key) ?? []).Keys.Take(2).Append("nope").ToArray();
                    (await kit.Client.RemoveFieldsAsync(key, some, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                    RemoveFields(model, key, some);
                    break;
                case 3:
                    (await kit.Client.RemoveAsync(key, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
                    model.Remove(key);
                    break;
                default:
                    (await kit.Client.ReadAsync(key, null, Ct)).Fields.Should().Match(f => ReadsAs(f, model.GetValueOrDefault(key)));
                    break;
            }

            if (step % 10 == 9)
            {
                await AssertAllAsync(kit, keys, model);
            }
        }

        await AssertAllAsync(kit, keys, model);
        foreach (var state in new[] { "S4", "S5" })
        {
            await kit.Kit.SetStateAsync(state);
            await AssertAllAsync(kit, keys, model, promoting: false);
        }
    }

    private static void RemoveFields(Dictionary<string, Dictionary<string, string>> model, string key, string[] fields)
    {
        if (!model.TryGetValue(key, out var held))
        {
            return;
        }

        foreach (var field in fields)
        {
            held.Remove(field);
        }

        if (held.Count == 0)
        {
            model.Remove(key);
        }
    }

    private static bool ReadsAs(IDictionary<string, Item?>? read, Dictionary<string, string>? expected) =>
        expected is null ? read is null : read is not null && read.ToDictionary(f => f.Key, f => f.Value?.Name).OrderBy(f => f.Key).SequenceEqual(expected.ToDictionary(f => f.Key, f => (string?)f.Value).OrderBy(f => f.Key));

    private static async Task AssertAllAsync(KeyMoveHashKit kit, string[] keys, Dictionary<string, Dictionary<string, string>> model, bool promoting = true)
    {
        foreach (var key in keys)
        {
            var read = await kit.Client.ReadAsync(key, null, Ct);
            ReadsAs(read.Fields, model.GetValueOrDefault(key)).Should().BeTrue($"{key} reads as {string.Join(',', (model.GetValueOrDefault(key) ?? []).Keys)} but read {string.Join(',', read.Fields?.Keys ?? [])}");
        }

        _ = promoting;
    }

    private static HashWriteOptions Replace(DateTimeOffset? deadline = null) => new(HashCacheSetOption.KeyReplace, deadline);

    private static HashWriteOptions Merge(DateTimeOffset? deadline = null) => new(HashCacheSetOption.HashReplace, deadline);
}
