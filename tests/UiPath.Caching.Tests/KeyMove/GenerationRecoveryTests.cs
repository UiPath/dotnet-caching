using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// Reassert and Recreate hand on the generations of the committed record, and write the previous-generation fields with them so the abandon of a recovery that stopped before its commit restores them.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GenerationRecoveryTests(RedisContainerFixture fixture)
{
    private static readonly SequenceOptions StopBeforeCommit = new(BeforeCommit: _ => Task.FromResult<TransitionResult?>(new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "stopped before the commit")));

    public static TheoryData<string, string, string?, string?> Variants() => new()
    {
        { "S3", "fwd", "gen-c", null },
        { "S1", "rev", null, "gen-c" },
        { "S0", "none", null, null },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task The_abandon_of_a_Reassert_that_stopped_before_its_commit_restores_the_generations(string state, string direction, string? fg, string? rg)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        await WriteCommittedAsync(kit, state, direction, fg, rg);

        var result = await kit.Recovery.ReassertAsync(kit.Group, "state lost", null, null, null, CancellationToken.None, StopBeforeCommit);
        var stopped = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        result.Transition!.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        stopped.Durable.Should().BeFalse();
        await AssertAbandonRestoresAsync(kit, stopped, state, direction, fg, rg);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task The_abandon_of_a_Recreate_that_stopped_before_its_commit_restores_the_generations(string state, string direction, string? fg, string? rg)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var identity = await WriteCommittedAsync(kit, state, direction, fg, rg);

        var result = await kit.Recovery.RecreateAsync(kit.Group, [identity], ["a"], null, CancellationToken.None, StopBeforeCommit);
        var stopped = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        result.Transition!.Outcome.Should().Be(TransitionOutcome.BarrierStuck);
        await AssertAbandonRestoresAsync(kit, stopped, state, direction, fg, rg);
    }

    [Fact]
    public async Task Recreate_takes_the_generation_of_a_committed_record_and_gives_a_new_one_when_the_record_is_not_committed()
    {
        await using var committed = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var identity = await WriteCommittedAsync(committed, "S3", "fwd", "gen-c", null);
        await using var uncommitted = await StateKit.StandaloneAsync(fixture, useScripts: true);
        var other = await WriteCommittedAsync(uncommitted, "S3", "fwd", "gen-c", null, commit: false);

        await committed.Recovery.RecreateAsync(committed.Group, [identity], ["a"], null, CancellationToken.None);
        await uncommitted.Recovery.RecreateAsync(uncommitted.Group, [other], ["a"], null, CancellationToken.None);
        var fromCommitted = (await committed.Store.ReadAsync(committed.Group, CancellationToken.None))!;
        var fromUncommitted = (await uncommitted.Store.ReadAsync(uncommitted.Group, CancellationToken.None))!;

        fromCommitted.ForwardGeneration.Should().Be("gen-c");
        fromUncommitted.ForwardGeneration.Should().NotBe("gen-c").And.Be(fromUncommitted.Id);
    }

    private static async Task AssertAbandonRestoresAsync(StateKit kit, StateRecord stopped, string state, string direction, string? fg, string? rg)
    {
        stopped.Field(StateFields.PrevFg).Should().Be(fg ?? string.Empty);
        stopped.Field(StateFields.PrevRg).Should().Be(rg ?? string.Empty);

        var abandoned = await kit.Sequence.AbandonAsync(kit.Group, false, 1, new SequenceOptions(), CancellationToken.None);
        var restored = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

        abandoned.Outcome.Should().Be(TransitionOutcome.Completed, abandoned.Detail);
        restored.State.Should().Be(state);
        restored.Direction.Should().Be(direction);
        restored.ForwardGeneration.Should().Be(fg ?? string.Empty);
        restored.RetiringGeneration.Should().Be(rg ?? string.Empty);
    }

    // A control record of the state, with the generations it shows, committed or not; the state hash is whatever the test makes of it.
    private static async Task<PeerIdentity> WriteCommittedAsync(StateKit kit, string state, string direction, string? fg, string? rg, bool commit = true)
    {
        const long Ep = 40;
        var id = "committed-" + Guid.NewGuid().ToString("N")[..8];
        var record = kit.Names.Control(kit.Group, 0);
        await kit.Scripts.ControlSyncAsync(record, new ControlRecord(Ep, id, state, direction, direction == "none" ? "0" : "1", "S0", "none", "0", "39", "earlier-id", [("a", "g1")])
        {
            BindingField = fg is null && rg is null ? string.Empty : "fg",
            ForwardGeneration = fg ?? string.Empty,
            RetiringGeneration = rg ?? string.Empty,
        });
        if (commit)
        {
            await kit.Scripts.ControlCommitAsync(record, Ep, id);
        }

        return new PeerIdentity(state, direction, Ep, id);
    }
}
