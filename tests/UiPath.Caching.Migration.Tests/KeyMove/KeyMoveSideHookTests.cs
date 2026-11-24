using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A source command selected before a scheduling hook still runs if availability changes at the hook.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveSideHookTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_source_that_drops_at_the_ReadSource_hook_still_answers_its_read()
    {
        var link = new Link();
        await using var kit = await OpenAsync(link);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S1", nPop: 0);
        await kit.Db.StringSetAsync(kit.L("k"), "v");
        kit.Schedule.Hook = (step, _) => Drop(link, step, SchedulePointStep.ReadSource);

        var read = await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value?.Name.Should().Be("v");
        link.Up.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_source_that_drops_at_the_ProbeSource_hook_still_answers_its_probe(bool batch)
    {
        var link = new Link();
        await using var kit = await OpenAsync(link);
        await kit.PrepareAsync("k");
        await kit.SetStateAsync("S3");
        kit.Schedule.Hook = (step, _) => Drop(link, step, SchedulePointStep.ProbeSource);

        var read = batch ? (await kit.Client.ReadManyAsync(["k"], Ct))["k"] : await kit.Client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        link.Up.Should().BeFalse();
    }

    private static Task Drop(Link link, SchedulePointStep step, SchedulePointStep at)
    {
        if (step == at)
        {
            link.Up = false;
        }

        return Task.CompletedTask;
    }

    private Task<KeyMoveKit> OpenAsync(Link link) => KeyMoveKit.CreateAsync(
        fixture,
        source: k => new InPlaceMoveSide<Item>(
            new StubPlane(k.Group, new EpochGate()) { Data = k.SideData with { IsConnected = () => link.Up } },
            k.Group,
            k.L,
            ItemCodec.Instance,
            null,
            k.Clock));

    private sealed class Link
    {
        public bool Up { get; set; } = true;
    }
}
