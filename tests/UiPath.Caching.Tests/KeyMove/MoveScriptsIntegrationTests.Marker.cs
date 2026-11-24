using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public partial class MoveScriptsIntegrationTests
{
    private const string MarkerField = "sm:src-key";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_setm_sets_the_key_the_fence_and_the_marker_in_one_step_and_keeps_the_peak(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);

        var reply = await SetMarkedAsync(env, "b1");

        reply.Code.Should().Be(1);
        reply.Marker.Should().Be("id1|b1");
        (await env.Db.StringGetAsync(env.Dst)).Should().Be("v1");
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("b1");
        (await env.Db.HashGetAsync(env.Ctl, MarkerField)).Should().Be("id1|b1");
        (await env.Db.HashGetAsync(env.Ctl, "pk")).Should().Be(reply.Length.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_setm_refuses_only_a_first_attempt_at_the_limit_and_then_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env, markers: 0);
        await env.Db.StringSetAsync(env.Fence, "before");

        (await SetMarkedAsync(env, "b1", first: true)).Code.Should().Be(MoveScripts.RecordFull);

        (await env.Db.StringGetAsync(env.Fence)).Should().Be("before");
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
        (await env.Scripts.LimitSetAsync(env.Ctl, MoveTestEnvironment.Id, await env.Db.HashLengthAsync(env.Ctl) + 1)).Should().Be(1);
        (await SetMarkedAsync(env, "b2", first: true)).Code.Should().Be(1, "a lowered or raised limit applies to the command at its execution");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_setm_never_refuses_a_rerun_and_not_a_first_attempt_whose_field_exists(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env, markers: 0);

        (await SetMarkedAsync(env, "b1", first: false)).Code.Should().Be(1);
        (await SetMarkedAsync(env, "b2", first: true)).Code.Should().Be(1, "the field exists, so it is no new marker");

        (await env.Db.HashGetAsync(env.Ctl, MarkerField)).Should().Be("id1|b2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_setm_on_a_record_with_no_limit_refuses_a_first_attempt_and_takes_a_rerun(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await SetMarkedAsync(env, "b1", first: true)).Code.Should().Be(MoveScripts.RecordFull);
        (await SetMarkedAsync(env, "b2", first: false)).Code.Should().Be(1);
    }

    [Fact]
    public async Task Bump_setm_refuses_a_late_command_and_changes_nothing()
    {
        await using var env = await NewEnvironmentAsync(useScripts: true);
        await LimitAsync(env);
        var now = await env.ServerNowAsync();

        (await SetMarkedAsync(env, "b1", deadline: now - 1_000)).Code.Should().Be(MoveScripts.Late);

        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
        (await SetMarkedAsync(env, "b2", deadline: now + 60_000)).Code.Should().Be(1);
    }

    [Fact]
    public async Task The_fallback_cannot_refuse_a_late_command_which_is_the_residual_R14()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        await LimitAsync(env);
        var now = await env.ServerNowAsync();

        (await SetMarkedAsync(env, "b1", deadline: now - 1_000)).Code.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_setm_refuses_a_record_with_no_binding_value_and_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await env.Db.HashSetAsync(env.Ctl, "bf", "missing");

        (await SetMarkedAsync(env, "b1")).Code.Should().Be(MoveScripts.NoBinding);

        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
        await env.Db.HashSetAsync(env.Ctl, "missing", string.Empty);
        (await SetMarkedAsync(env, "b2")).Code.Should().Be(MoveScripts.NoBinding, "an empty value binds nothing");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_setm_binds_the_marker_to_the_field_the_record_names(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await env.Db.HashSetAsync(env.Ctl, [new("bf", "fg"), new("fg", "g9")]);

        (await SetMarkedAsync(env, "b1")).Marker.Should().Be("g9|b1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bump_del_removes_the_marker_field_it_is_given(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await SetMarkedAsync(env, "b1");

        await env.Scripts.BumpAsync(env.Keys, MoveTestEnvironment.Id, "b2", 30_000, BumpOperation.Delete, markerField: MarkerField);

        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_answers_a_current_marker_and_ignores_an_obsolete_one(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await SetMarkedAsync(env, "b1");

        var live = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n1", 30_000, MarkerField);
        await env.Db.HashSetAsync(env.Ctl, MarkerField, "id0|b0");
        var obsolete = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n2", 30_000, MarkerField);
        var none = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n3", 30_000);

        live.Code.Should().Be(MoveScripts.LiveSupersession);
        live.Marker.ToString().Should().Be("id1|b1");
        live.Captured.Should().BeFalse();
        obsolete.Captured.Should().BeTrue();
        none.Captured.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cap_trusts_a_marker_only_under_the_binding_the_record_names(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new("bf", "fg"), new("fg", "g9"), new(MarkerField, "id1|n0")]);

        var byIdentity = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n1", 30_000, MarkerField);
        await env.Db.HashSetAsync(env.Ctl, MarkerField, "g9|n0");
        var byGeneration = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n2", 30_000, MarkerField);

        byIdentity.Captured.Should().BeTrue("the record binds to fg, and the marker was set under the identity");
        byGeneration.Code.Should().Be(MoveScripts.LiveSupersession);
    }

    [Fact]
    public async Task The_fallback_capture_reads_the_marker_after_the_fence_token()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        var order = new List<string>();
        var db = InterceptingProxy.Wrap(env.Db, (m, _) => m.Name == nameof(IDatabase.CreateBatch)
            ? InterceptingProxy.Wrap(env.Db.CreateBatch(), (bm, args) =>
            {
                if (bm.Name == nameof(IBatch.StringGetAsync) && args[0] is RedisKey key && key == env.Fence)
                {
                    order.Add("token");
                }
                else if (bm.Name == nameof(IBatch.HashGetAsync) && args[1] is RedisValue field && field == MarkerField)
                {
                    order.Add("marker");
                }

                return InterceptingProxy.Proceed;
            })
            : InterceptingProxy.Proceed);
        var scripts = new MoveScripts(db, useScripts: false, env.ServerVersion);

        await scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n1", 30_000, MarkerField);

        order.Should().Equal("token", "marker");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_marker_set_before_the_capture_is_seen_and_one_removed_after_its_delete_leaves_a_capture_under_the_writes_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await env.Db.StringSetAsync(env.Fence, "stale-token");
        await SetMarkedAsync(env, "b1");
        var duringWrite = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n1", 30_000, MarkerField);

        // The writer's second step and its unmark have run.
        (await env.Scripts.UnmarkAsync(env.Ctl, MarkerField, "id1|b1")).Should().Be(1);
        var afterwards = await env.Scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n2", 30_000, MarkerField);

        duringWrite.Code.Should().Be(MoveScripts.LiveSupersession);
        afterwards.Captured.Should().BeTrue();
        afterwards.Token.Should().Be("b1", "the write's fence is the token in force");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unmark_removes_the_exact_value_only_and_checks_no_identity(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, MarkerField, "id0|n0");

        (await env.Scripts.UnmarkAsync(env.Ctl, MarkerField, "id0|other")).Should().Be(0);
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeTrue();
        (await env.Scripts.UnmarkAsync(env.Ctl, MarkerField, "id0|n0")).Should().Be(1, "an obsolete marker is removed when its exact value is named");
        (await env.Scripts.UnmarkAsync(env.Ctl, MarkerField, "id0|n0")).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unmark_many_removes_the_pairs_that_still_match(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new("sm:a", "x|1"), new("sm:b", "x|2"), new("sm:c", "x|3")]);

        var removed = await env.Scripts.UnmarkManyAsync(env.Ctl, [("sm:a", "x|1"), ("sm:b", "x|other"), ("sm:c", "x|3"), ("sm:d", "x|4")]);

        removed.Should().Be(2);
        (await env.Db.HashExistsAsync(env.Ctl, "sm:b")).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Smgc_removes_only_an_obsolete_marker_and_answers_minus_one_for_a_live_or_retiring_one(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new("sm:live", "id1|n"), new("sm:old", "id0|n"), new("sm:bar", "id10|n"), new("rg", "r5"), new("sm:ret", "r5|n")]);

        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:live", "id1|n")).Should().Be(-1);
        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:ret", "r5|n")).Should().Be(-1, "the generation is retiring");
        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:old", "id0|n")).Should().Be(1);
        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:old", "id0|n")).Should().Be(0);
        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:bar", "id10|other")).Should().Be(0);
        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:bar", "id10|n")).Should().Be(1, "id10 is not id1");
        (await env.Db.HashExistsAsync(env.Ctl, "sm:live")).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Smgc_reads_the_binding_from_the_field_the_record_names(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new("bf", "fg"), new("fg", "g9"), new("sm:id", "id1|n"), new("sm:gen", "g9|n")]);

        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:gen", "g9|n")).Should().Be(-1);
        (await env.Scripts.SupersessionGcAsync(env.Ctl, "sm:id", "id1|n")).Should().Be(1, "under the generation binding a marker of the identity is obsolete");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Smgc_many_answers_one_code_per_pair(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new("sm:live", "id1|n"), new("sm:old", "id0|n"), new("sm:chg", "id0|m")]);

        var codes = await env.Scripts.SupersessionGcManyAsync(env.Ctl, [("sm:live", "id1|n"), ("sm:old", "id0|n"), ("sm:chg", "id0|x"), ("sm:none", "id0|n")]);

        codes.Should().Equal(-1, 1, 0, 0);
    }

    [Fact]
    public async Task The_fallback_smgc_does_not_remove_a_marker_that_an_identity_change_made_live_during_its_transaction()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        await env.Db.HashSetAsync(env.Ctl, MarkerField, "id9|n");
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, touch: other => other.HashSetAsync(env.Ctl, "id", "id9"));
        var scripts = new MoveScripts(harness.Database, useScripts: false, env.ServerVersion);

        var code = await scripts.SupersessionGcAsync(env.Ctl, MarkerField, "id9|n");

        code.Should().Be(-1);
        harness.Aborted.Should().Be(1);
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeTrue();
    }

    [Fact]
    public async Task The_fallback_unmark_does_not_remove_a_marker_that_changed_during_its_transaction()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        await env.Db.HashSetAsync(env.Ctl, MarkerField, "id1|n");
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, touch: other => other.HashSetAsync(env.Ctl, MarkerField, "id1|newer"));
        var scripts = new MoveScripts(harness.Database, useScripts: false, env.ServerVersion);

        var code = await scripts.UnmarkAsync(env.Ctl, MarkerField, "id1|n");

        code.Should().Be(0);
        (await env.Db.HashGetAsync(env.Ctl, MarkerField)).Should().Be("id1|newer");
    }

    [Fact]
    public async Task The_fallback_bump_setm_meets_the_limit_that_filled_up_during_its_transaction_on_a_first_attempt_only()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        await LimitAsync(env, markers: 1);
        await using var first = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, touch: other => other.HashSetAsync(env.Ctl, "sm:filler", "x|y"));
        var firstScripts = new MoveScripts(first.Database, useScripts: false, env.ServerVersion);

        var refused = await SetMarkedAsync(env, "b1", first: true, scripts: firstScripts);

        refused.Code.Should().Be(MoveScripts.RecordFull);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        await env.Db.HashDeleteAsync(env.Ctl, "sm:filler");
        await using var rerun = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, touch: other => other.HashSetAsync(env.Ctl, "sm:filler", "x|y"));
        var rerunScripts = new MoveScripts(rerun.Database, useScripts: false, env.ServerVersion);

        (await SetMarkedAsync(env, "b2", first: false, scripts: rerunScripts)).Code.Should().Be(1);
        (await env.Db.HashGetAsync(env.Ctl, MarkerField)).Should().Be("id1|b2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcdelg_deletes_L_only_under_a_binding_that_is_current_or_retiring(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(env.Ctl, "rg", "r5");
        await env.Db.StringSetAsync(src, "x");

        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "id0")).Should().Be(MoveScripts.BindingEnded);
        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, string.Empty)).Should().Be(MoveScripts.BindingEnded);
        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, "other", "id1")).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "r5")).Should().Be(1);
        await env.Db.StringSetAsync(src, "y");
        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "id1")).Should().Be(1);
        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "id1")).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Srcdelg_reads_the_binding_from_the_field_the_record_names(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var src = env.Make("src");
        await env.Db.HashSetAsync(env.Ctl, [new("bf", "fg"), new("fg", "g9")]);
        await env.Db.StringSetAsync(src, "x");

        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "id1")).Should().Be(MoveScripts.BindingEnded);
        (await env.Scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "g9")).Should().Be(1);
    }

    [Fact]
    public async Task The_fallback_srcdelg_does_not_delete_when_the_binding_ended_during_its_transaction()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "x");
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, touch: other => other.HashSetAsync(env.Ctl, "id", "id2"));
        var scripts = new MoveScripts(harness.Database, useScripts: false, env.ServerVersion);

        var code = await scripts.SourceDeleteGuardedAsync(src, env.Ctl, MoveTestEnvironment.Id, "id1");

        code.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(src)).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Limset_sets_the_limit_under_the_identity(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);

        (await env.Scripts.LimitSetAsync(env.Ctl, "other", 50)).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.HashExistsAsync(env.Ctl, "lim")).Should().BeFalse();
        (await env.Scripts.LimitSetAsync(env.Ctl, MoveTestEnvironment.Id, 50)).Should().Be(1);
        (await env.Db.HashGetAsync(env.Ctl, "lim")).Should().Be("50");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlcompact_rewrites_a_small_record_resets_its_peak_and_refuses_a_large_one(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await SetMarkedAsync(env, "b1");
        await env.Scripts.UnmarkAsync(env.Ctl, MarkerField, "id1|b1");
        await env.Db.HashSetAsync(env.Ctl, "pk", "5000");
        var before = (await env.Db.HashGetAllAsync(env.Ctl)).Where(e => e.Name != "pk").OrderBy(e => (string)e.Name!, StringComparer.Ordinal).ToArray();

        (await env.Scripts.CompactControlAsync(env.Ctl, "other")).Should().Be(MoveScripts.IdentityMismatch);
        var length = await env.Scripts.CompactControlAsync(env.Ctl, MoveTestEnvironment.Id);

        length.Should().Be(await env.Db.HashLengthAsync(env.Ctl));
        (await env.Db.HashGetAsync(env.Ctl, "pk")).Should().Be(length.ToString(CultureInfo.InvariantCulture));
        (await env.Db.HashGetAllAsync(env.Ctl)).Where(e => e.Name != "pk").OrderBy(e => (string)e.Name!, StringComparer.Ordinal).Should().Equal(before);
        await env.Db.HashSetAsync(env.Ctl, Enumerable.Range(0, 2100).Select(i => new HashEntry($"f{i}", "v")).ToArray());
        (await env.Scripts.CompactControlAsync(env.Ctl, MoveTestEnvironment.Id)).Should().Be(MoveScripts.CompactRefused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_drops_the_gates_of_its_list_and_leaves_markers_and_unlisted_fields(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await env.Db.HashSetAsync(env.Ctl, [new(MarkerField, "id1|n"), new("c:zz", "OPEN:x")]);

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", ("c9", "g2"), ("c8", "g2")))).Should().Be(1);

        (await env.Db.HashGetAsync(env.Ctl, "cl")).Should().Be("c8+c9");
        (await env.Db.HashExistsAsync(env.Ctl, "c:" + MoveTestEnvironment.Cid)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeTrue();
        (await env.Db.HashExistsAsync(env.Ctl, "c:zz")).Should().BeTrue("the list names the gates; an unlisted one is not scanned for");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_scans_a_record_that_has_no_list(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        await env.Db.HashSetAsync(env.Ctl, [new("ep", "1"), new("id", "id1"), new("c:a", "OPEN:x"), new("c:b", "OPEN:y")]);

        (await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", ("c9", "g2")))).Should().Be(1);

        (await env.Db.HashExistsAsync(env.Ctl, "c:a")).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, "c:b")).Should().BeFalse();
        (await env.Db.HashGetAsync(env.Ctl, "cl")).Should().Be("c9");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ctlsync_writes_the_limit_and_the_binding_fields_it_is_given_and_removes_the_binding_fields_it_is_not(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts, openControl: false);
        var first = MoveTestEnvironment.Record(1, "id1", ("c1", "g1")) with { Limit = 99, BindingField = "fg", ForwardGeneration = "g9", RetiringGeneration = "g5" };
        await env.Scripts.ControlSyncAsync(env.Ctl, first);

        (await env.Db.HashGetAsync(env.Ctl, ["lim", "bf", "fg", "rg"])).Select(v => (string?)v).Should().Equal("99", "fg", "g9", "g5");
        await env.Scripts.ControlSyncAsync(env.Ctl, MoveTestEnvironment.Record(2, "id2", ("c1", "g2")));

        (await env.Db.HashGetAsync(env.Ctl, ["lim", "bf", "fg", "rg"])).Select(v => (string?)v).Should().Equal("99", null, null, null);
    }

    private static Task<MarkedBump> SetMarkedAsync(MoveTestEnvironment env, string nonce, bool first = true, long deadline = 0, MoveScripts? scripts = null) =>
        (scripts ?? env.Scripts).BumpMarkedAsync(env.Keys, MoveTestEnvironment.Id, nonce, 30_000, "v1", MoveScripts.NoExpiry, MarkerField, first, deadline);

    // A record of a core build at S3: the identity is the binding, and the limit leaves room for the given number of markers.
    private static async Task LimitAsync(MoveTestEnvironment env, long markers = 100) =>
        (await env.Scripts.LimitSetAsync(env.Ctl, MoveTestEnvironment.Id, await env.Db.HashLengthAsync(env.Ctl) + 1 + markers)).Should().Be(1);
}
