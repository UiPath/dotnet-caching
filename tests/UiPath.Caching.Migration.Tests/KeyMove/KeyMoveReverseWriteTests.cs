using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The write and delete forms of the reverse states, with both scripts and the transaction fallback.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveReverseWriteTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S1_rev_writes_N_then_deletes_L_under_the_reverse_fence(bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", direction: "rev");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v0");
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "before");

        var result = await kit.Client.SetAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceSet, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.N("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.GetAsync(ReverseFence(kit, "k"))).Should().NotBe("before");
        (await kit.FenceAsync("k")).Should().BeNull("a reverse write never replaces the forward fence");
    }

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S3", true)]
    [InlineData("S4", false)]
    public async Task A_reverse_delete_deletes_N_then_L_under_the_reverse_fence(string state, bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v0");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(ReverseFence(kit, "k"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("S1", true)]
    [InlineData("S1", false)]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    [InlineData("S4", false)]
    public async Task A_reverse_delete_retries_against_the_observed_fence_and_replaces_it(string state, bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        var fence = ReverseFence(kit, "k");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v0");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");
        await kit.Db.StringSetAsync(fence, "token-before");
        DropConnectionAtBump(kit);

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("k").Should().BeTrue();
        (await kit.GetAsync(fence)).Should().Be("token-before");
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v0");
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        await ReconnectAndRunRetriesAsync(kit);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.GetAsync(fence)).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reverse_delete_retry_preserves_a_newer_value_and_fence(bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        var fence = ReverseFence(kit, "k");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v0");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");
        await kit.Db.StringSetAsync(fence, "token-before");
        DropConnectionAtBump(kit);

        (await kit.Client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("k").Should().BeTrue();
        await kit.Db.StringSetAsync(fence, "newer");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v2");

        await ReconnectAndRunRetriesAsync(kit);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v2");
        (await kit.GetAsync(fence)).Should().Be("newer");
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    [InlineData("S4", false)]
    public async Task S3_and_S4_rev_delete_N_then_set_L_in_its_own_format_for_a_cache_with_an_origin(string state, bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "before");

        var result = await kit.Client.SetAsync("k", new Item("v1"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(10)), Ct);

        result.Outcome.Should().Be(WriteOutcome.Committed);
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.SourceDelete, SchedulePointStep.FenceObserve, SchedulePointStep.Bump);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        (await kit.GetAsync(ReverseFence(kit, "k"))).Should().NotBe("before");
        (await kit.FenceAsync("k")).Should().BeNull();
    }

    [Theory]
    [InlineData("S1", true, false)]
    [InlineData("S1", false, false)]
    [InlineData("S1", true, true)]
    [InlineData("S1", false, true)]
    [InlineData("S3", true, false)]
    [InlineData("S3", false, false)]
    [InlineData("S3", true, true)]
    [InlineData("S3", false, true)]
    [InlineData("S4", true, false)]
    [InlineData("S4", false, false)]
    [InlineData("S4", true, true)]
    [InlineData("S4", false, true)]
    public async Task Reverse_writes_are_closed_for_a_cache_without_an_origin(string state, bool scripts, bool delete)
    {
        await using var kit = await CreateAsync(scripts, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state, direction: "rev");
        var fence = ReverseFence(kit, "k");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v0");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");
        await kit.Db.StringSetAsync(fence, "before");

        var write = async () => await (delete
            ? kit.Client.RemoveAsync("k", Ct)
            : kit.Client.SetAsync("k", new Item("v1"), null, Ct));

        await write.Should().ThrowAsync<CacheUnavailableException>();
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v0");
        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v0");
        (await kit.GetAsync(fence)).Should().Be("before");
        kit.Schedule.Steps.Should().BeEmpty();
    }

    [Theory]
    [InlineData("S1", "ReverseS1")]
    [InlineData("S2", "Unavailable")]
    [InlineData("S3", "ReverseS3")]
    [InlineData("S4", "ReverseS4")]
    [InlineData("S5", "Unavailable")]
    public async Task A_reverse_state_plans_its_own_write_mode_and_a_union_read(string state, string expected)
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.SetStateAsync(state, direction: "rev");

        kit.Client.PlanFor(write: true).Mode.ToString().Should().Be(expected);
        kit.Client.PlanFor(write: false).Mode.Should().Be(KeyMoveMode.Union);
        kit.Client.NoFallback.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failed_reverse_bump_is_retried_under_the_reverse_fence_it_observed(bool scripts)
    {
        await using var kit = await CreateAsync(scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");
        await kit.Db.StringSetAsync(ReverseFence(kit, "k"), "token-before");
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };

        var result = await kit.Client.SetAsync("k", new Item("v1"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("k").Should().BeTrue();
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();
        (await kit.ExistsAsync(kit.L("k"))).Should().BeFalse();

        kit.Schedule.Hook = null;
        kit.Connected = true;
        await kit.Client.Retries.RunDueAsync(all: true);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v1");
        (await kit.FenceAsync("k")).Should().BeNull();
    }

    [Fact]
    public async Task A_reverse_retry_whose_fence_moved_is_superseded_and_never_overwrites_L()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        var fence = ReverseFence(kit, "k");
        await kit.Db.StringSetAsync(fence, "token-before");
        DropConnectionAtBump(kit);

        (await kit.Client.SetAsync("k", new Item("v1"), null, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        kit.Client.Retries.IsPending("k").Should().BeTrue();
        await kit.Db.StringSetAsync(fence, "newer");
        await kit.Db.StringSetAsync(kit.L("k"), "l:v2");

        await ReconnectAndRunRetriesAsync(kit);

        kit.Client.Retries.Count.Should().Be(0);
        (await kit.GetAsync(kit.L("k"))).Should().Be("l:v2");
        (await kit.GetAsync(fence)).Should().Be("newer");
    }

    [Fact]
    public async Task A_value_the_in_place_format_cannot_hold_is_not_sent()
    {
        await using var kit = await CreateAsync(scripts: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3", direction: "rev");
        await kit.Db.StringSetAsync(kit.N("k"), "n:v0");

        (await kit.Client.SetAsync("k", new Item(TaggedItemCodec.Unencodable), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        kit.Schedule.Steps.Should().BeEmpty();
        (await kit.GetAsync(kit.N("k"))).Should().Be("n:v0");
    }

    private static void DropConnectionAtBump(KeyMoveKit kit)
    {
        kit.Schedule.Hook = (step, _) =>
        {
            if (step == SchedulePointStep.Bump)
            {
                kit.Connected = false;
            }

            return Task.CompletedTask;
        };
    }

    private static Task ReconnectAndRunRetriesAsync(KeyMoveKit kit)
    {
        kit.Schedule.Hook = null;
        kit.Connected = true;
        return kit.Client.Retries.RunDueAsync(all: true);
    }

    private static string ReverseFence(KeyMoveKit kit, string key) => kit.Names.ReverseFence(kit.L(key)).ToString();

    private Task<KeyMoveKit> CreateAsync(bool scripts, bool noOrigin = false) =>
        KeyMoveKit.CreateAsync(fixture, scripts, noOrigin, inPlace: new TaggedItemCodec("l:"), native: new TaggedItemCodec("n:"));
}
