using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

/// <summary>The recovery side of an instance move on two servers: the claim steps with their baselines, the claim recovery, the upkeep and the state recovery, over a <see cref="TwoServerKit"/>.</summary>
internal sealed class InstanceRecoveryKit : IAsyncDisposable
{
    public const string Token = "0123456789abcdef0123456789abcdef";

    private InstanceRecoveryKit(TwoServerKit kit, ManualTimeProvider time)
    {
        Kit = kit;
        Time = time;
        var dstNames = kit.Placement.Names(RecordSide.Dst);
        var srcNames = kit.Placement.Names(RecordSide.Src);
        SrcClaim = kit.Conns.Claim(RecordSide.Src, kit.Placement, kit.SrcCaps);
        DstClaim = kit.Conns.Claim(RecordSide.Dst, kit.Placement, kit.DstCaps);
        Tokens = new StateAcquisitionTokens(new MoveScripts(kit.RawDst, true, kit.DstCaps.Version), kit.RawDst, g => kit.Placement.State(g).Key);
        Claim = new InstanceMoveClaim(SrcClaim, DstClaim, kit.Generations, _ => true, Alert, Tokens);
        Baselines = new RedisAdmissionBaselines(kit.RawDst, dstNames, new MoveScripts(kit.RawDst, true, kit.DstCaps.Version));
        Steps = new AdmissionRecordingClaim(Claim, TwoServerKit.App, kit.Generations, Baselines, _ => SrcClaim.ReadAsync(), Alert, Tokens);
        Reader = new InstanceClaimReader(SrcClaim, DstClaim);
        Mirror = new InstanceMirrorRepair(DstClaim, _ => true, Alert);
        Recovery = new InstanceClaimRecovery(Steps, Mirror, Reader, kit.Options, time, Alert, Baselines, TwoServerKit.App, Tokens, kit.Generations, Tokens, g => Fence?.Invoke(g));
        var topology = new MultiplexerTopology(kit.DstMux, string.Empty, true, kit.DstCaps.Version);
        var sampler = new ControlSampler(kit.RawDst, dstNames, topology);
        Store = new StateStore(kit.RawDst, dstNames, new MoveScripts(kit.RawDst, true, kit.DstCaps.Version), sampler, time);
        StateReader = new TwoServerStateReader(Store, sampler, new SrcRecordReader(kit.RawSrc, srcNames));
        Records = new InstanceRecordReader(new ControlRecordReader(kit.RawSrc, srcNames), new ControlRecordReader(kit.RawDst, dstNames), kit.Views.Changes);
        Upkeep = new InstanceControlUpkeep(Records, kit.Sequence, Store, Recovery, kit.Options, time, (kind, detail) => Reports.Add((kind, detail)));
        StateRecovery = new StateRecovery(Store, StateReader, Records, kit.Sequence, time, Reader, Recovery);
    }

    public TwoServerKit Kit { get; }

    public ManualTimeProvider Time { get; }

    /// <summary>The lease fence the recovery works under; null for none.</summary>
    public Func<string, LeaseFence?>? Fence { get; set; }

    public ClaimStore SrcClaim { get; }

    public ClaimStore DstClaim { get; }

    public StateAcquisitionTokens Tokens { get; }

    public InstanceMoveClaim Claim { get; }

    public RedisAdmissionBaselines Baselines { get; }

    public AdmissionRecordingClaim Steps { get; }

    public InstanceClaimReader Reader { get; }

    public InstanceMirrorRepair Mirror { get; }

    public InstanceClaimRecovery Recovery { get; }

    public StateStore Store { get; }

    public TwoServerStateReader StateReader { get; }

    public InstanceRecordReader Records { get; }

    public InstanceControlUpkeep Upkeep { get; }

    public StateRecovery StateRecovery { get; }

    public List<(string Group, string Code, string Detail)> Alerts { get; } = [];

    public List<(string Kind, string Detail)> Reports { get; } = [];

    public RedisKey SrcClaimKey => Kit.Placement.Claim(RecordSide.Src).Key;

    public RedisKey DstClaimKey => Kit.Placement.Claim(RecordSide.Dst).Key;

    public static async Task<InstanceRecoveryKit> CreateAsync(TwoServerFixture fixture, TwoServerKit.KitOptions? options = null)
    {
        var kit = await TwoServerKit.CreateAsync(fixture, options);
        return new InstanceRecoveryKit(kit, kit.Time);
    }

    /// <summary>A move that ran to S1: the claim pended and opened with <see cref="Token"/>, the state at S1 <c>dur=1</c> carrying <c>ct</c> and <c>mv</c>, and the records on both servers.</summary>
    public async Task<StateRecord> MoveToS1Async()
    {
        var seeded = await Kit.SeedAsync();
        (await Steps.PendAsync(Kit.Group, Token, InstanceMoveClaim.Descriptor, null, CancellationToken.None)).Should().Be(ClaimOutcome.Done);
        (await Steps.OpenAsync(Kit.Group, Token, false, null, CancellationToken.None)).Should().Be(ClaimOutcome.Done);
        var result = await Kit.RunAsync(seeded, TwoServerKit.Target(seeded) with { Claim = Token, Move = InstanceMoveClaim.Descriptor });
        result.Outcome.Should().Be(TransitionOutcome.Completed);
        return (await Kit.ReadStateAsync())!;
    }

    public async Task<string?> SrcFieldAsync() => (string?)await Kit.RawSrc.HashGetAsync(SrcClaimKey, "im:" + Kit.Group, CommandFlags.DemandMaster);

    public async Task<string?> MirrorFieldAsync() => (string?)await Kit.RawDst.HashGetAsync(DstClaimKey, "imd:" + Kit.Group, CommandFlags.DemandMaster);

    /// <summary>Stores a baseline as it is, with no check, to stand for one that an earlier run left.</summary>
    public Task SeedBaselineAsync(string kind, AdmissionBaseline baseline) =>
        Kit.RawDst.StringSetAsync(Kit.Placement.Names(RecordSide.Dst).Baseline(TwoServerKit.App, kind), baseline.Text);

    public Task FlushStateAsync() => Task.WhenAll(Kit.RawDst.KeyDeleteAsync(Kit.StateKey), Kit.RawDst.KeyDeleteAsync(Kit.DstRecord), Kit.RawDst.KeyDeleteAsync(DstClaimKey));

    public ValueTask DisposeAsync() => Kit.DisposeAsync();

    private void Alert(string group, string code, string detail) => Alerts.Add((group, code, detail));
}
