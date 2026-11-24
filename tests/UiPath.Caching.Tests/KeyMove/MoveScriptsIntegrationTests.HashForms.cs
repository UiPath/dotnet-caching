using System.Globalization;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public partial class MoveScriptsIntegrationTests
{
    private static readonly HashEntry[] SingleEntry = [new HashEntry("a", "1")];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_replaces_the_hash_the_fence_and_sets_the_marker_in_one_step_and_keeps_the_peak(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "old", "1");

        var reply = await SetMarkedHashAsync(env, key, "b1");

        reply.Code.Should().Be(1);
        reply.Result.Should().Be(1);
        reply.Marker.Should().Be("id1|b1");
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["a"] = "1" });
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("b1");
        (await env.Db.HashGetAsync(env.Ctl, MarkerField)).Should().Be("id1|b1");
        (await env.Db.HashGetAsync(env.Ctl, "pk")).Should().Be(reply.Length.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_returns_the_version_it_counted_next_to_the_marker(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        var key = env.Make("hash");

        var reply = await env.Scripts.HashWriteMarkedAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, version: "ver", seed: 100, entries: SingleEntry), new FenceBump(env.Fence, "b1", 30_000), MarkerField, first: true, deadline: 0);

        reply.Code.Should().Be(1);
        reply.Result.Should().Be(101);
        reply.Marker.Should().Be("id1|b1");
        (await env.Db.HashGetAsync(key, "ver")).Should().Be("101");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_refuses_only_a_first_attempt_at_the_limit_and_then_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env, markers: 0);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "old", "1");
        await env.Db.StringSetAsync(env.Fence, "before");

        (await SetMarkedHashAsync(env, key, "b1", first: true)).Code.Should().Be(MoveScripts.RecordFull);

        (await env.Db.StringGetAsync(env.Fence)).Should().Be("before");
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["old"] = "1" });
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
        (await env.Scripts.LimitSetAsync(env.Ctl, MoveTestEnvironment.Id, await env.Db.HashLengthAsync(env.Ctl) + 1)).Should().Be(1);
        (await SetMarkedHashAsync(env, key, "b2", first: true)).Code.Should().Be(1, "a raised limit applies to the command at its execution");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_never_refuses_a_rerun_and_not_a_first_attempt_whose_field_exists(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env, markers: 0);
        var key = env.Make("hash");

        (await SetMarkedHashAsync(env, key, "b1", first: false)).Code.Should().Be(1);
        (await SetMarkedHashAsync(env, key, "b2", first: true)).Code.Should().Be(1, "the field exists, so it is no new marker");

        (await env.Db.HashGetAsync(env.Ctl, MarkerField)).Should().Be("id1|b2");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_on_a_record_with_no_limit_refuses_a_first_attempt_and_takes_a_rerun(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");

        (await SetMarkedHashAsync(env, key, "b1", first: true)).Code.Should().Be(MoveScripts.RecordFull);
        (await SetMarkedHashAsync(env, key, "b2", first: false)).Code.Should().Be(1);
    }

    [Fact]
    public async Task Hsetc_setm_refuses_a_late_command_and_changes_nothing()
    {
        await using var env = await NewEnvironmentAsync(useScripts: true);
        await LimitAsync(env);
        var key = env.Make("hash");
        var now = await env.ServerNowAsync();

        (await SetMarkedHashAsync(env, key, "b1", deadline: now - 1_000)).Code.Should().Be(MoveScripts.Late);

        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
        (await SetMarkedHashAsync(env, key, "b2", deadline: now + 60_000)).Code.Should().Be(1);
    }

    [Fact]
    public async Task The_fallback_of_hsetc_setm_cannot_refuse_a_late_command_which_is_the_residual_R14()
    {
        await using var env = await NewEnvironmentAsync(useScripts: false);
        await LimitAsync(env);
        var key = env.Make("hash");
        var now = await env.ServerNowAsync();

        (await SetMarkedHashAsync(env, key, "b1", deadline: now - 1_000)).Code.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_refuses_a_record_with_no_binding_value_and_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(env.Ctl, "bf", "missing");

        (await SetMarkedHashAsync(env, key, "b1")).Code.Should().Be(MoveScripts.NoBinding);

        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_binds_the_marker_to_the_field_the_record_names(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        await env.Db.HashSetAsync(env.Ctl, [new("bf", "fg"), new("fg", "g9")]);

        (await SetMarkedHashAsync(env, env.Make("hash"), "b1")).Marker.Should().Be("g9|b1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hsetc_setm_of_another_identity_changes_nothing(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        await LimitAsync(env);
        var key = env.Make("hash");

        var reply = await env.Scripts.HashWriteMarkedAsync(key, env.Ctl, "other", Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, "b1", 30_000), MarkerField, first: true, deadline: 0);

        reply.Code.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
        (await env.Db.HashExistsAsync(env.Ctl, MarkerField)).Should().BeFalse();
    }

    [Fact]
    public async Task Hsetc_setm_is_only_for_a_whole_replace()
    {
        await using var env = await NewEnvironmentAsync(useScripts: true);

        var merge = () => env.Scripts.HashWriteMarkedAsync(env.Make("hash"), env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.HashReplace, entries: SingleEntry), new FenceBump(env.Fence, "b1", 30_000), MarkerField, first: true, deadline: 0);

        await merge.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hsetc_lands_while_the_fence_holds_the_nonce_of_the_first_attempt(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.StringSetAsync(env.Fence, "n1");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, "n1", 30_000), new FenceRetry("seen"));

        result.Should().Be(1);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["a"] = "1" });
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n1");
        (await env.Db.KeyTimeToLiveAsync(env.Fence))!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hsetc_lands_while_the_fence_holds_the_token_seen_before_the_first_attempt(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.StringSetAsync(env.Fence, "seen");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, "n1", 30_000), new FenceRetry("seen"));

        result.Should().Be(1);
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n1", "the retry leaves the fence at the nonce of the first attempt");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_retried_hsetc_is_superseded_by_another_fence_or_none_and_changes_nothing(bool useScripts, bool fenceExists)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "newer", "1");
        if (fenceExists)
        {
            await env.Db.StringSetAsync(env.Fence, "later-write");
        }

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, "n1", 30_000), new FenceRetry("seen"));

        result.Should().Be(MoveScripts.Superseded);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["newer"] = "1" });
        ((string?)await env.Db.StringGetAsync(env.Fence)).Should().Be(fenceExists ? "later-write" : null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hsetc_with_no_observed_token_matches_only_its_own_nonce(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.StringSetAsync(env.Fence, "other");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, "n1", 30_000), new FenceRetry(null));

        result.Should().Be(MoveScripts.Superseded);
        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hsetc_of_another_identity_is_refused_before_the_fence_is_looked_at(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, "other", Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, "n1", 30_000), new FenceRetry(null));

        result.Should().Be(MoveScripts.IdentityMismatch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hsetc_counts_the_version_like_the_first_attempt(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.StringSetAsync(env.Fence, "n1");

        var result = await env.Scripts.HashWriteAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, version: "ver", seed: 100, entries: SingleEntry), new FenceBump(env.Fence, "n1", 30_000), new FenceRetry(null));

        result.Should().Be(101);
    }

    [Fact]
    public async Task A_retried_hash_write_without_a_fence_is_refused_up_front()
    {
        await using var env = await NewEnvironmentAsync(useScripts: true);

        var write = () => env.Scripts.HashWriteAsync(env.Make("hash"), env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), fence: null, new FenceRetry(null));
        var delete = () => env.Scripts.HashDeleteFieldsAsync(env.Make("hash"), env.Ctl, MoveTestEnvironment.Id, ["a"], retry: new FenceRetry(null));

        await write.Should().ThrowAsync<ArgumentException>();
        await delete.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hdelc_deletes_while_the_fence_holds_either_token_and_leaves_the_nonce(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("b", "2")]);
        await env.Db.StringSetAsync(env.Fence, "seen");

        var result = await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a"], fence: new FenceBump(env.Fence, "n1", 30_000), retry: new FenceRetry("seen"));

        result.Should().Be(1);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["b"] = "2" });
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("n1");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_retried_hdelc_is_superseded_by_another_fence_or_none_and_deletes_nothing(bool useScripts, bool fenceExists)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("b", "2")]);
        if (fenceExists)
        {
            await env.Db.StringSetAsync(env.Fence, "later-write");
        }

        var result = await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a"], fence: new FenceBump(env.Fence, "n1", 30_000), retry: new FenceRetry("seen"));

        result.Should().Be(MoveScripts.Superseded);
        (await FieldsAsync(env, key)).Should().Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        ((string?)await env.Db.StringGetAsync(env.Fence)).Should().Be(fenceExists ? "later-write" : null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hdelc_of_the_last_field_removes_a_hash_left_with_only_its_count(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, [new HashEntry("a", "1"), new HashEntry("ver", "5")]);
        await env.Db.StringSetAsync(env.Fence, "n1");

        var result = await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, MoveTestEnvironment.Id, ["a"], versionField: "ver", versionSeed: 100, fence: new FenceBump(env.Fence, "n1", 30_000), retry: new FenceRetry(null));

        result.Should().Be(1);
        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_hdelc_of_another_identity_is_refused_before_the_fence_is_looked_at(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var key = env.Make("hash");
        await env.Db.HashSetAsync(key, "a", "1");
        await env.Db.StringSetAsync(env.Fence, "n1");

        var result = await env.Scripts.HashDeleteFieldsAsync(key, env.Ctl, "other", ["a"], fence: new FenceBump(env.Fence, "n1", 30_000), retry: new FenceRetry(null));

        result.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.HashExistsAsync(key, "a")).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_without_a_fence_fills_an_absent_hash_and_touches_no_fence(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync(px: null);

        var result = await env.Scripts.FillHashAsync(env.Dst, env.Ctl, staging, MoveTestEnvironment.Id, 30_000, fence: null);

        result.Should().Be(MoveScripts.Published);
        (await FieldsAsync(env, env.Dst)).Should().Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        (await env.Db.KeyExistsAsync(staging)).Should().BeFalse();
        (await env.Db.KeyTimeToLiveAsync(env.Dst))!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        (await env.Db.KeyExistsAsync(env.Fence)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_without_a_fence_or_a_life_persists_and_leaves_a_fence_key_alone(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync(px: 5_000);
        await env.Db.StringSetAsync(env.Fence, "held");

        var result = await env.Scripts.FillHashAsync(env.Dst, env.Ctl, staging, MoveTestEnvironment.Id, MoveScripts.NoExpiry, fence: null);

        result.Should().Be(MoveScripts.Published);
        (await env.Db.KeyTimeToLiveAsync(env.Dst)).Should().BeNull();
        (await env.Db.StringGetAsync(env.Fence)).Should().Be("held");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_without_a_fence_leaves_a_held_destination_and_drops_staging(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync();
        await env.Db.HashSetAsync(env.Dst, "held", "1");

        var result = await env.Scripts.FillHashAsync(env.Dst, env.Ctl, staging, MoveTestEnvironment.Id, 30_000, fence: null);

        result.Should().Be(MoveScripts.DestinationExists);
        (await FieldsAsync(env, env.Dst)).Should().Equal(new Dictionary<string, string> { ["held"] = "1" });
        (await env.Db.KeyExistsAsync(staging)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fillh_without_a_fence_reports_a_missing_staging_key_and_a_foreign_identity(bool useScripts)
    {
        await using var env = await NewEnvironmentAsync(useScripts);
        var staging = await env.StageHashAsync();

        (await env.Scripts.FillHashAsync(env.Dst, env.Ctl, env.Make("absent"), MoveTestEnvironment.Id, 30_000, fence: null)).Should().Be(MoveScripts.StagingMissing);
        (await env.Scripts.FillHashAsync(env.Dst, env.Ctl, staging, "other", 30_000, fence: null)).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
        (await env.Db.KeyExistsAsync(staging)).Should().BeTrue();
    }

    private static Task<MarkedHashWrite> SetMarkedHashAsync(MoveTestEnvironment env, RedisKey key, string nonce, bool first = true, long deadline = 0) =>
        env.Scripts.HashWriteMarkedAsync(key, env.Ctl, MoveTestEnvironment.Id, Write(HashCacheSetOption.KeyReplace, entries: SingleEntry), new FenceBump(env.Fence, nonce, 30_000), MarkerField, first, deadline);
}
