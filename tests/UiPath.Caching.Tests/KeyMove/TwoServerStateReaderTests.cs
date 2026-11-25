using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The two-server sample on two real servers, and a Dst that goes down after the first Src write and before DURABLE.</summary>
[Collection("TwoServers")]
[Trait("Category", "Integration")]
public sealed class TwoServerStateReaderTests(TwoServerFixture fixture)
{
    [Fact]
    public async Task The_sample_takes_the_highest_ep_of_the_src_record_and_the_dst_records_and_flags_two_ids_at_one_ep()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        await kit.RawSrc.HashSetAsync(kit.SrcRecord, Record(8, "src", "S1", "1", "0"));
        await kit.RawDst.HashSetAsync(kit.DstRecord, Record(5, "dst", "S1", "1", "0"));

        var higher = (await rk.StateReader.SampleAsync(kit.Group, TestContext.Current.CancellationToken))!;

        higher.Should().Match<ControlSample>(s => s.Ep == 8 && s.Id == "src" && s.SrcHome && s.SrcDurable && s.NPop == 0);

        await kit.RawDst.HashSetAsync(kit.DstRecord, Record(8, "other", "S1", "1", "0"));

        (await rk.StateReader.SampleAsync(kit.Group, TestContext.Current.CancellationToken))!.Conflicting.Should().BeTrue();
        (await rk.StateReader.ProbeAsync(kit.Group, TestContext.Current.CancellationToken))!.Conflicting.Should().BeTrue();
    }

    [Theory]
    [InlineData("X1", "S1", "0")]
    [InlineData("X3", "S3", "1")]
    public async Task A_dst_that_goes_down_before_durable_is_followed_from_the_src_record_writes_wait_then_fail_closed_and_recovery_completes(string transition, string target, string np)
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var down = false;
        var dstDown = InterceptingProxy.Wrap(kit.RawDst, (_, _) => down ? throw Down() : InterceptingProxy.Proceed);
        var names = kit.Placement.Names(RecordSide.Dst);
        var topology = new MultiplexerTopology(kit.DstMux, string.Empty, true, kit.DstCaps.Version);
        var sampler = new ControlSampler(dstDown, names, topology);
        var store = new StateStore(dstDown, names, new MoveScripts(kit.RawDst, true, kit.DstCaps.Version), sampler, kit.Time);
        var reader = new TwoServerStateReader(store, sampler, new SrcRecordReader(kit.RawSrc, kit.Placement.Names(RecordSide.Src)));
        var monitor = new GroupStateMonitor(reader, kit.Options, time);
        monitor.Register([new GroupRegistration(kit.Group, false, true, InstanceMove: true)]);
        var seeded = await kit.SeedAsync();
        await monitor.StepAsync(kit.Group, TestContext.Current.CancellationToken);
        monitor.RulesFor(kit.Group).Writes.Should().Be(WriteRule.Follow);

        // The Dst fails after the first Src write: the Src record carries the new identity, the state on the Dst never became durable.
        var cut = new TwoServerKit.Cut(RecordSide.Dst, "DURABLE", TwoServerKit.CutMode.Down);
        kit.Cuts.Add(cut);
        var run = target == "S1" ? TwoServerKit.Target(seeded) : TwoServerKit.Target(seeded, "S3") with { NPop = 1 };
        var stuck = await kit.RunAsync(seeded, run);
        stuck.Outcome.Should().NotBe(TransitionOutcome.Completed, transition);
        down = true;
        await monitor.StepAsync(kit.Group, TestContext.Current.CancellationToken);

        AssertDstOutageRules(monitor.RulesFor(kit.Group), np, run.Ep, seeded.Ep);

        // Recovery: the Dst returns, the leader finishes the transition, and the node follows the new durable state.
        down = false;
        cut.Lift();
        (await kit.Sequence.ResumeAsync(kit.Group, null, TestContext.Current.CancellationToken)).Outcome.Should().Be(TransitionOutcome.Completed);
        await monitor.StepAsync(kit.Group, TestContext.Current.CancellationToken);
        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting!.Ep == run.Ep && r.Alerts.Count == 0);
    }

    [Fact]
    public async Task A_dst_that_goes_down_with_the_src_record_at_dur_zero_waits_the_transition_wait_then_fails_closed_while_reads_continue()
    {
        await using var rk = await InstanceRecoveryKit.CreateAsync(fixture);
        var kit = rk.Kit;
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var down = false;
        var dstDown = InterceptingProxy.Wrap(kit.RawDst, (_, _) => down ? throw Down() : InterceptingProxy.Proceed);
        var names = kit.Placement.Names(RecordSide.Dst);
        var sampler = new ControlSampler(dstDown, names, new MultiplexerTopology(kit.DstMux, string.Empty, true, kit.DstCaps.Version));
        var store = new StateStore(dstDown, names, new MoveScripts(kit.RawDst, true, kit.DstCaps.Version), sampler, kit.Time);
        var monitor = new GroupStateMonitor(new TwoServerStateReader(store, sampler, new SrcRecordReader(kit.RawSrc, kit.Placement.Names(RecordSide.Src))), kit.Options, time);
        monitor.Register([new GroupRegistration(kit.Group, false, true, InstanceMove: true)]);
        var seeded = await kit.SeedAsync();
        await monitor.StepAsync(kit.Group, TestContext.Current.CancellationToken);

        // The Src CTLCOMMIT is cut, so the Src record stays at dur=0 under the new identity.
        kit.Cuts.Add(new TwoServerKit.Cut(RecordSide.Src, "CTLCOMMIT", TwoServerKit.CutMode.Down));
        await kit.RunAsync(seeded, TwoServerKit.Target(seeded, "S3") with { NPop = 1 });
        down = true;
        await monitor.StepAsync(kit.Group, TestContext.Current.CancellationToken);

        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.WaitThenFailClosed && r.Reads == ReadRule.Follow && r.Acting!.Ep == seeded.Ep);
        time.Advance(kit.Options.TransitionWait + TimeSpan.FromSeconds(1));
        monitor.RulesFor(kit.Group).Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Reads == ReadRule.Follow);
    }

    private static void AssertDstOutageRules(GroupRules rules, string np, long targetEp, long previousEp)
    {
        rules.Alerts.Should().Contain("dst_unreachable");
        if (np == "0")
        {
            rules.Should().Match<GroupRules>(r => r.Writes == WriteRule.Follow && r.Acting!.Ep == targetEp && r.Acting.State == "S1", "an np=0 identity under a matching durable Src record is adopted");
        }
        else
        {
            rules.Should().Match<GroupRules>(r => r.Writes == WriteRule.FailClosed && r.Reads == ReadRule.UnionNativeFirst && r.Acting!.Ep == previousEp, "np=1 stays closed, with reads over the union");
        }
    }

    private static HashEntry[] Record(long ep, string id, string state, string durable, string np) =>
        [new("ep", ep), new("id", id), new("st", state), new("dir", "fwd"), new("dur", durable), new("np", np)];

    private static RedisConnectionException Down() => new(ConnectionFailureType.SocketFailure, CommandFlags.None, "the Dst is down", null, CommandStatus.Unknown);
}
