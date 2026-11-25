using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A fill uses NX and returns the stored value when the key already exists.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveFillTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S1_fills_L_only_when_absent_and_a_losing_fill_returns_the_stored_value_and_changes_nothing(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);
        var box = new FillBox<Item>();

        (await kit.Client.FillAsync("k", new Item("fill"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMinutes(5)), box, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.GetAsync(kit.L("k"))).Should().Be("fill");

        (await kit.Client.FillAsync("k", new Item("late"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        box.Stored!.Name.Should().Be("fill");
        (await kit.GetAsync(kit.L("k"))).Should().Be("fill");
        (await kit.Db.KeyTimeToLiveAsync(kit.L("k")))!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
    }

    [Theory]
    [InlineData("S3", true)]
    [InlineData("S3", false)]
    [InlineData("S4", true)]
    public async Task S3_and_S4_fill_N_when_absent_and_replace_the_fence_and_never_delete_L(string state, bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync(state);
        await kit.Db.StringSetAsync(kit.Names.ForwardFence(kit.N("k")), "before");
        await kit.Db.StringSetAsync(kit.L("k"), "other-L");

        (await kit.Client.FillAsync("k", new Item("fill"), null, null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        (await kit.GetAsync(kit.N("k"))).Should().Be("fill");
        (await kit.FenceAsync("k")).Should().NotBe("before");
        (await kit.GetAsync(kit.L("k"))).Should().Be("other-L");
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.Fill);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_losing_fill_in_S3_returns_the_stored_N_and_clobbers_nothing(bool scripts)
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.N("k"), "stored");
        var box = new FillBox<Item>();

        (await kit.Client.FillAsync("k", new Item("fill"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        box.Stored!.Name.Should().Be("stored");
        (await kit.GetAsync(kit.N("k"))).Should().Be("stored");
    }

    [Fact]
    public async Task A_fill_refuses_a_promoter_that_captured_before_it_and_the_fill_value_wins()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        await kit.Db.StringSetAsync(kit.L("k"), "v0");
        kit.Schedule.Hook = async (step, nth) =>
        {
            if (step == SchedulePointStep.Publish && nth == 1)
            {
                // The fill lands between the promoter's capture and its publish.
                await kit.Client.FillAsync("k", new Item("fill"), null, null, Ct);
            }
        };

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Value!.Name.Should().Be("fill");
        (await kit.GetAsync(kit.N("k"))).Should().Be("fill");
    }

    [Fact]
    public async Task S5_fills_N_with_a_plain_NX_and_a_losing_fill_returns_the_stored_value()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S5");
        var box = new FillBox<Item>();

        (await kit.Client.FillAsync("k", new Item("fill"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await kit.Client.FillAsync("k", new Item("late"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        box.Stored!.Name.Should().Be("fill");
        kit.Schedule.Steps.Should().Equal(SchedulePointStep.NativeWrite, SchedulePointStep.NativeWrite, SchedulePointStep.ReadDestinationOnMaster);
    }

    [Fact]
    public async Task Fill_follows_the_write_gate_and_skips_expired_values()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, noOrigin: true);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S2", nPop: 0);
        var closed = async () => await kit.Client.FillAsync("k", new Item("fill"), null, null, Ct);
        await closed.Should().ThrowAsync<CacheUnavailableException>();

        await kit.SetStateAsync("S3");
        (await kit.Client.FillAsync("k", new Item("fill"), kit.Clock.ToDateTimeOffset(TimeSpan.FromMilliseconds(-5)), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await kit.ExistsAsync(kit.N("k"))).Should().BeFalse();

        await kit.SetStateAsync("S0");
        await kit.Client.FillAsync("k", new Item("fill"), null, null, Ct);
        kit.Plain.Sets.Should().Be(1);
    }
}
