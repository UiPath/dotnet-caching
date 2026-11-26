using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// A write that starts while the group is held, and goes on in S1 or S2 once it is let through, is judged by the capability of L in the state it lands in, not in the one it started in.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveHashWaitTests(RedisFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("set", "S1", "null", true)]
    [InlineData("set", "S1", "empty", true)]
    [InlineData("set", "S1", "populated", true)]
    [InlineData("set", "S2", "null", true)]
    [InlineData("set", "S2", "empty", true)]
    [InlineData("set", "S2", "populated", true)]
    [InlineData("fill", "S1", "null", true)]
    [InlineData("fill", "S1", "empty", true)]
    [InlineData("fill", "S1", "populated", true)]
    [InlineData("fill", "S2", "null", true)]
    [InlineData("fill", "S2", "empty", true)]
    [InlineData("fill", "S2", "populated", true)]
    [InlineData("set", "S1", "null", false)]
    [InlineData("set", "S1", "empty", false)]
    [InlineData("set", "S1", "populated", false)]
    [InlineData("set", "S2", "empty", false)]
    [InlineData("set", "S2", "populated", false)]
    [InlineData("fill", "S1", "null", false)]
    [InlineData("fill", "S1", "empty", false)]
    [InlineData("fill", "S1", "populated", false)]
    [InlineData("fill", "S2", "empty", false)]
    [InlineData("fill", "S2", "populated", false)]
    public async Task A_write_that_waits_and_lands_in_S1_or_S2_is_judged_by_the_capability_of_L_there(string operation, string state, string kind, bool envelope)
    {
        await using var kit = await KeyMoveHashKit.CreateAsync(fixture, options: new KeyMoveOptions { AdmissionPoll = TimeSpan.FromMilliseconds(10) }, inPlace: new ItemHashCodec(false, systemFields: !envelope));
        await kit.Kit.PrepareAsync("k");
        await kit.Kit.SetStateAsync(state, nPop: 0);
        var open = kit.Kit.Plane.Rules;
        Dictionary<string, string?>? metadata = null;
        if (kind == "empty")
        {
            metadata = [];
        }
        else if (kind != "null")
        {
            metadata = new Dictionary<string, string?> { ["owner"] = "me" };
        }

        var options = new HashWriteOptions(HashCacheSetOption.KeyReplace, null, metadata);
        var fields = KeyMoveHashKit.Items(("a", "1"));

        // The group is held when the write starts: its plan is a wait, which refuses nothing yet.
        kit.Kit.Plane.Rules = open with { Writes = WriteRule.WaitThenFailClosed, WriteWait = TimeSpan.FromSeconds(5) };
        var write = operation == "set" ? kit.Client.SetAsync("k", fields, options, Ct).AsTask() : kit.Client.FillAsync("k", fields, options, null, Ct).AsTask();
        var admission = await kit.Kit.Time.NextTimerAsync(Ct);
        write.IsCompleted.Should().BeFalse("the write is waiting");
        kit.Kit.Plane.Rules = open;
        kit.Kit.Time.Advance(admission);

        if (envelope && metadata is not null)
        {
            var thrown = await Assert.ThrowsAsync<NotSupportedException>(() => write);
            thrown.Message.Should().Contain(kit.Kit.Cid);
            (await kit.Kit.ExistsAsync(kit.L("k"))).Should().BeFalse("nothing was stored");
            return;
        }

        (await write).Outcome.Should().Be(WriteOutcome.Committed);
        var stored = await kit.AllFieldsAsync(kit.L("k"));
        stored.Should().ContainKey("a");
        if (envelope || metadata is null)
        {
            stored.Keys.Where(f => f.StartsWith('_')).Should().BeEmpty("no metadata was given, or L cannot hold any");
        }
        else
        {
            stored.Should().ContainKey("_metadata_", "L is a library format and holds the metadata it was given");
        }
    }
}
