using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The conditional retry: a retried value write applies only while the fence is the nonce its first attempt set or the token seen just before it, on the scripts and on the fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveRetryTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BUMPIF_applies_over_the_nonce_of_the_first_attempt_and_over_the_observed_token_and_refuses_anything_else(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        var fence = kit.Names.ForwardFence(kit.N("k"));
        var keys = new FencedKeys(fence, kit.N("k"), kit.Names.ControlFor(kit.Group, kit.N("k")));

        await kit.Db.StringSetAsync(fence, "A");
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", "B", 60_000, BumpOperation.Set, "by-nonce", -1)).Should().Be(1);
        (await kit.GetAsync(kit.N("k"))).Should().Be("by-nonce");
        (await kit.FenceAsync("k")).Should().Be("A");

        await kit.Db.StringSetAsync(fence, "B");
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", "B", 60_000, BumpOperation.Set, "by-observed", 60_000)).Should().Be(1);
        (await kit.GetAsync(kit.N("k"))).Should().Be("by-observed");
        (await kit.FenceAsync("k")).Should().Be("A");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromSeconds(50));

        await kit.Db.StringSetAsync(fence, "newer");
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", "B", 60_000, BumpOperation.Set, "lost", -1)).Should().Be(MoveScripts.Superseded);
        await kit.Db.KeyDeleteAsync(fence);
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", null, 60_000, BumpOperation.Set, "lost", -1)).Should().Be(MoveScripts.Superseded);
        (await kit.Scripts.BumpIfFenceAsync(keys, kit.Id, "A", string.Empty, 60_000, BumpOperation.Set, "lost", -1)).Should().Be(MoveScripts.Superseded);
        (await kit.GetAsync(kit.N("k"))).Should().Be("by-observed");
        (await kit.Scripts.BumpIfFenceAsync(keys, "stale-identity", "A", "B", 60_000, BumpOperation.Set, "lost", -1)).Should().Be(MoveScripts.IdentityMismatch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retry_applies_when_the_fence_is_still_the_token_seen_before_the_first_attempt(bool scripts)
    {
        await using var kit = await RetryPendingAsync(scripts, fence: "token-before");

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.GetAsync(kit.N("k"))).Should().Be("v1");
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Another_nodes_stale_retry_meeting_a_newer_write_is_dropped_and_counted_and_the_newer_value_stays(bool scripts)
    {
        await using var kit = await RetryPendingAsync(scripts, fence: "token-before");
        using var meter = new MeterCollector(kit.Cid);

        // Another node writes the key in the meantime: its BUMP replaces the fence.
        var other = new FencedKeys(kit.Names.ForwardFence(kit.N("k")), kit.N("k"), kit.Names.ControlFor(kit.Group, kit.N("k")));
        (await kit.Scripts.BumpAsync(other, kit.Id, "other-node-nonce", 60_000, BumpOperation.Set, "newer", -1)).Should().BeGreaterThanOrEqualTo(0);

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.GetAsync(kit.N("k"))).Should().Be("newer");
        kit.Client.Retries.Count.Should().Be(0);
        meter.Count("move_retry_superseded").Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retry_is_dropped_when_the_other_node_deleted_the_key_and_replaced_the_fence(bool scripts)
    {
        await using var kit = await RetryPendingAsync(scripts, fence: "token-before");
        var other = new FencedKeys(kit.Names.ForwardFence(kit.N("k")), kit.N("k"), kit.Names.ControlFor(kit.Group, kit.N("k")));
        await kit.Scripts.BumpAsync(other, kit.Id, "other-node-nonce", 60_000, BumpOperation.Delete);

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Client.Retries.Count.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retry_is_dropped_when_the_fence_is_absent_even_if_it_was_absent_before_the_first_attempt(bool scripts)
    {
        await using var kit = await RetryPendingAsync(scripts, fence: null);
        using var meter = new MeterCollector(kit.Cid);

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        kit.Client.Retries.Count.Should().Be(0);
        meter.Count("move_retry_superseded").Should().Be(1);
    }

    [Fact]
    public async Task A_retried_delete_applies_while_the_fence_is_still_the_token_seen_before_the_first_attempt()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        await kit.Db.StringSetAsync(kit.N("k"), "v0");
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;

        await kit.Client.Retries.RunDueAsync(all: true);

        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
    }

    // The origin write whose second step could not be sent: L is gone and N is not written yet.
    private async Task<KeyMoveKit> RetryPendingAsync(bool scripts, string? fence)
    {
        var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        if (fence is not null)
        {
            await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), fence);
        }

        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Schedule.Hook = null;
        kit.Connected = true;
        return kit;
    }
}
