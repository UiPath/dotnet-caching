using StackExchange.Redis;
using UiPath.Caching.PhaseB;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GuardedDeleteTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_identity_guard_deletes_under_the_identity_and_refuses_another_or_an_absent_record(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts);
        var key = env.Make("data");
        await env.Db.StringSetAsync(key, "v");

        (await env.Scripts.GuardedDeleteAsync(key, env.Ctl, DeleteGuard.Identity, "other")).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(key)).Should().BeTrue();
        (await env.Scripts.GuardedDeleteAsync(key, env.Make("nothing"), DeleteGuard.Identity, MoveTestEnvironment.Id)).Should().Be(MoveScripts.IdentityMismatch, "an absent record is a mismatch");
        (await env.Scripts.GuardedDeleteAsync(key, env.Ctl, DeleteGuard.Identity, MoveTestEnvironment.Id)).Should().Be(1);
        (await env.Scripts.GuardedDeleteAsync(key, env.Ctl, DeleteGuard.Identity, MoveTestEnvironment.Id)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_guard_that_allows_no_record_deletes_when_none_exists_and_still_refuses_another_identity(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts);
        var key = env.Make("data");
        await env.Db.StringSetAsync(key, "v");
        var absent = env.Make("absent-record");

        (await env.Scripts.GuardedDeleteAsync(key, env.Ctl, DeleteGuard.IdentityOrNoRecord, "other")).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(key)).Should().BeTrue();
        (await env.Scripts.GuardedDeleteAsync(key, absent, DeleteGuard.IdentityOrNoRecord, "any")).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_epoch_guard_deletes_a_record_not_above_the_epoch_and_keeps_a_newer_one(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts);

        (await env.Scripts.GuardedDeleteAsync(env.Ctl, env.Ctl, DeleteGuard.EpochAtMost, "0")).Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(env.Ctl)).Should().BeTrue("the record is at epoch 1");
        (await env.Scripts.GuardedDeleteAsync(env.Ctl, env.Ctl, DeleteGuard.EpochAtMost, "1")).Should().Be(1);
        (await env.Db.KeyExistsAsync(env.Ctl)).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lease_in_the_guard_slot_must_still_be_the_callers(bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts);
        var lease = env.Make("lease");
        await env.Db.StringSetAsync(lease, "mine");
        var target = env.Ctl;

        (await env.Scripts.GuardedDeleteAsync(target, target, DeleteGuard.Identity, MoveTestEnvironment.Id, new LeaseFence(lease, "theirs"))).Should().Be(MoveScripts.LeaseNotHeld);
        (await env.Db.KeyExistsAsync(target)).Should().BeTrue();
        (await env.Scripts.GuardedDeleteAsync(target, target, DeleteGuard.Identity, MoveTestEnvironment.Id, new LeaseFence(lease, "mine"))).Should().Be(1);
    }

    [Fact]
    public async Task One_aborted_exec_with_every_predicate_holding_is_sent_again_in_the_fallback()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var key = env.Make("data");
        await env.Db.StringSetAsync(key, "v");
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1);

        var code = await new MoveScripts(harness.Database, useScripts: false, env.ServerVersion).GuardedDeleteAsync(key, env.Ctl, DeleteGuard.Identity, MoveTestEnvironment.Id);

        code.Should().Be(1);
        harness.Aborted.Should().Be(1);
        harness.Executions.Should().BeGreaterThanOrEqualTo(2);
        (await env.Db.KeyExistsAsync(key)).Should().BeFalse();
    }

    [Fact]
    public async Task An_exec_that_aborts_on_every_attempt_throws_and_deletes_nothing()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var key = env.Make("data");
        await env.Db.StringSetAsync(key, "v");
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: int.MaxValue);

        var act = () => new MoveScripts(harness.Database, useScripts: false, env.ServerVersion).GuardedDeleteAsync(key, env.Ctl, DeleteGuard.Identity, MoveTestEnvironment.Id);

        await act.Should().ThrowAsync<RedisException>();
        harness.Executions.Should().Be(MoveScripts.MaxWatchAborts);
        (await env.Db.KeyExistsAsync(key)).Should().BeTrue();
    }

    [Fact]
    public async Task A_record_that_changes_identity_under_the_aborted_exec_refuses_the_delete()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var key = env.Make("data");
        await env.Db.StringSetAsync(key, "v");
        await using var harness = await WatchAbortHarness.CreateAsync(env, fixture.ConnectionString, toForce: 1, touch: other => other.HashSetAsync(env.Ctl, "id", "moved"));

        var code = await new MoveScripts(harness.Database, useScripts: false, env.ServerVersion).GuardedDeleteAsync(key, env.Ctl, DeleteGuard.Identity, MoveTestEnvironment.Id);

        code.Should().Be(MoveScripts.IdentityMismatch);
        (await env.Db.KeyExistsAsync(key)).Should().BeTrue("the identity changed between the abort and the retry");
    }
}
