using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GroupDataOpsTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "")]
    [InlineData(true, "app:")]
    [InlineData(false, "{shard}:")]
    public async Task A_purge_deletes_the_owned_keys_of_the_managed_caches_and_leaves_every_other_key(bool useScripts, string prefix)
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts, prefix);
        var owned = new[] { await kit.SetAsync(MoveSide.Native, "a", "k1"), await kit.SetAsync(MoveSide.Native, "b", "k2", TimeSpan.FromHours(1)) };
        var unmanaged = await kit.SetAsync(MoveSide.Native, "c", "k3");
        var legacy = await kit.SetAsync(MoveSide.Legacy, "a", "k4");
        var state = await kit.StateAsync();

        (await kit.Data.CountNativeAsync(kit.Group, Ct)).Should().Be(2);
        await kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), Ct);

        foreach (var key in owned)
        {
            (await kit.ExistsAsync(key)).Should().BeFalse(key);
        }

        (await kit.ExistsAsync(unmanaged)).Should().BeTrue("an unmanaged cache is not owned");
        (await kit.ExistsAsync(legacy)).Should().BeTrue("a native purge never touches L");
        (await kit.Data.CountNativeAsync(kit.Group, Ct)).Should().Be(0);
        (await kit.StateAsync()).State.Should().Be("S1", "PurgeData never touches the state");
        (await kit.ControlDb.HashGetAsync(kit.Kit.Names.Control(kit.Group, 0), "ep")).HasValue.Should().BeTrue("nor a control record");
    }

    [Fact]
    public async Task The_legacy_purge_deletes_only_the_owned_L_keys()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var l = await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        var n = await kit.SetAsync(MoveSide.Native, "a", "k2");

        await kit.Data.PurgeLegacyAsync(kit.Group, new DataGuard((await kit.StateAsync()).Id, null), Ct);

        (await kit.ExistsAsync(l)).Should().BeFalse();
        (await kit.ExistsAsync(n)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_purge_decided_under_an_identity_the_state_left_deletes_nothing(bool useScripts)
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts);
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");
        var old = await kit.StateAsync();
        await kit.MoveToAsync("S2");

        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(old.Id, null), Ct);

        await act.Should().ThrowAsync<PurgeRefusedException>();
        (await kit.ExistsAsync(key)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_server_refuses_each_delete_whose_slot_holds_a_newer_identity_even_when_the_state_still_shows_the_old_one(bool useScripts)
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts);
        var keys = new[] { await kit.SetAsync(MoveSide.Native, "a", "k1"), await kit.SetAsync(MoveSide.Native, "a", "k2") };
        var state = await kit.StateAsync();
        await kit.ControlDb.HashSetAsync(kit.Kit.Names.Control(kit.Group, 0), "id", "newer");

        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), Ct);

        await act.Should().ThrowAsync<PurgeRefusedException>();
        foreach (var key in keys)
        {
            (await kit.ExistsAsync(key)).Should().BeTrue("the record of the slot is the authority, key by key");
        }
    }

    [Fact]
    public async Task A_lease_taken_over_before_the_run_stops_it_before_any_delete()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");
        var lead = kit.Kit.Names.Lead(kit.Group);
        await kit.Kit.Db.StringSetAsync(lead, "successor");

        var id = (await kit.StateAsync()).Id;
        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(id, new LeaseFence(lead, "stale-leader")), Ct);

        await act.Should().ThrowAsync<PurgeRefusedException>().WithMessage("*lease*");
        (await kit.ExistsAsync(key)).Should().BeTrue();
        await kit.Kit.Db.KeyDeleteAsync(lead);
    }

    [Fact]
    public async Task A_lease_lost_between_batches_stops_the_run_after_the_batch_in_flight()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true, batch: 1);
        var keys = new List<string>();
        for (var n = 0; n < 4; n++)
        {
            keys.Add(await kit.SetAsync(MoveSide.Native, "a", "k" + n));
        }

        var lead = kit.Kit.Names.Lead(kit.Group);
        await kit.Kit.Db.StringSetAsync(lead, "me");
        var asked = 0;
        kit.Space.OnOwnerOf = _ =>
        {
            if (Interlocked.Increment(ref asked) == 3)
            {
                kit.Kit.Db.KeyDelete(lead);
            }
        };

        var id = (await kit.StateAsync()).Id;
        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(id, new LeaseFence(lead, "me")), Ct);

        await act.Should().ThrowAsync<PurgeRefusedException>();
        var left = 0;
        foreach (var key in keys)
        {
            left += await kit.ExistsAsync(key) ? 1 : 0;
        }

        left.Should().BeInRange(1, 3, "the run stopped at the first batch that found the lease gone");
    }

    [Fact]
    public async Task Keys_that_keep_appearing_end_the_purge_with_a_refusal_after_the_pass_limit()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.SetAsync(MoveSide.Native, "a", "k0");
        var n = 0;
        kit.Space.OnOwnerOf = _ => kit.Kit.Db.StringSet(kit.Prefix + kit.Space.PrefixOf(MoveSide.Native) + "a:late" + Interlocked.Increment(ref n), "v");

        var id = (await kit.StateAsync()).Id;
        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(id, null), Ct);

        await act.Should().ThrowAsync<PurgeRefusedException>().WithMessage("*passes*");
        await kit.Kit.Db.KeyDeleteAsync([.. Enumerable.Range(1, n + 1).Select(i => (RedisKey)(kit.Space.PrefixOf(MoveSide.Native) + "a:late" + i))]);
    }

    [Fact]
    public async Task The_operator_purge_runs_in_a_group_that_never_started_without_any_control_record_and_never_touches_the_state()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true, started: false);
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");
        (await kit.ControlDb.KeyExistsAsync(kit.Kit.Names.Control(kit.Group, 0))).Should().BeFalse();

        (await kit.Data.PurgeDataAsync(kit.Group, Ct)).Should().Be(PurgeOutcome.Purged);

        (await kit.ExistsAsync(key)).Should().BeFalse();
        (await kit.StateAsync()).State.Should().Be("S0");
    }

    [Fact]
    public async Task A_group_that_ran_and_came_back_to_S0_is_not_purged_around_a_lost_control_record_which_is_repaired_instead()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.MoveToAsync("S0");
        await kit.Kit.ControlDb.KeyDeleteAsync(kit.Kit.Names.Control(kit.Group, 0));
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");

        (await kit.Data.PurgeDataAsync(kit.Group, Ct)).Should().Be(PurgeOutcome.Refused);

        (await kit.ExistsAsync(key)).Should().BeTrue("S0 alone does not prove the group never started");
    }

    [Fact]
    public async Task The_never_started_permission_is_checked_inside_the_purge_boundary_not_only_by_the_caller()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await kit.MoveToAsync("S0");
        await kit.Kit.ControlDb.KeyDeleteAsync(kit.Kit.Names.Control(kit.Group, 0));
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");
        var id = (await kit.StateAsync()).Id;

        var act = () => kit.Data.PurgeNativeAsync(kit.Group, new DataGuard(id, null, AllowNoRecord: true), Ct);

        await act.Should().ThrowAsync<PurgeRefusedException>().WithMessage("*started*");
        (await kit.ExistsAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task The_operator_purge_still_refuses_a_record_of_another_identity_and_a_group_that_is_not_in_S0_or_has_no_state()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true, started: false);
        var key = await kit.SetAsync(MoveSide.Native, "a", "k1");
        await kit.ControlDb.HashSetAsync(kit.Kit.Names.Control(kit.Group, 0), "id", "other");

        (await kit.Data.PurgeDataAsync(kit.Group, Ct)).Should().Be(PurgeOutcome.Refused);
        (await kit.ExistsAsync(key)).Should().BeTrue();
        await kit.ControlDb.KeyDeleteAsync(kit.Kit.Names.Control(kit.Group, 0));
        await kit.MoveToAsync("S1");
        (await kit.Data.PurgeDataAsync(kit.Group, Ct)).Should().Be(PurgeOutcome.NotApplicable);
        (await kit.Data.PurgeDataAsync("g" + Guid.NewGuid().ToString("N")[..8], Ct)).Should().Be(PurgeOutcome.NoState);
    }

    [Theory]
    [InlineData("*", "\\*")]
    [InlineData("a[b]?", "a\\[b\\]\\?")]
    [InlineData("plain:", "plain:")]
    public void The_connector_prefix_is_escaped_in_the_scan_pattern(string prefix, string escaped) =>
        MasterScanner.EscapeGlob(prefix).Should().Be(escaped);
}
