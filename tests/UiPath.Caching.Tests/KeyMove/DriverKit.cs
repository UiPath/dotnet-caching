using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The leader's loop over a real state store with a simulated fleet and the seams faked; time is the kit's manual clock.
internal sealed class DriverKit : IAsyncDisposable
{
    private DriverKit(StateKit kit, StageDriverOptions options)
    {
        Kit = kit;
        Options = options;
        Fleet = new SimulatedFleet(kit.Store, kit.Group);
        Facts = new FakeGroupFacts { ReleaseFloorAttested = true };
        var flags = new FlagViews(options, kit.Time);
        Driver = new StageDriver(
            new StageDriverServices(kit.Store, kit.Scripts, kit.Names, kit.Sequence, kit.Preflight, kit.Ledger, Fleet, Facts, Claim, Data, Mover, _ => HoldsLease, null, null, (_, code, detail) => Alerts.Add((code, detail)), _ => Fence),
            options,
            kit.Options,
            flags,
            new ViewStability(kit.Time),
            kit.Time);
        Commands = new StageCommands(kit.Store, kit.Scripts, kit.Names, options);
    }

    public StateKit Kit { get; }

    public StageDriverOptions Options { get; }

    public SimulatedFleet Fleet { get; }

    public FakeGroupFacts Facts { get; }

    public FakeMoveClaim Claim { get; } = new();

    public FakeGroupData Data { get; } = new();

    public FakeMoverStatus Mover { get; } = new();

    public StageDriver Driver { get; }

    public StageCommands Commands { get; }

    public bool HoldsLease { get; set; } = true;

    public LeaseFence? Fence { get; set; }

    public List<(string Code, string Detail)> Alerts { get; } = [];

    public string Group => Kit.Group;

    public static async Task<DriverKit> CreateAsync(RedisContainerFixture fixture, bool useScripts = true, StageDriverOptions? options = null, KeyMoveStateOptions? state = null)
    {
        var kit = await StateKit.StandaloneAsync(fixture, useScripts, state ?? new KeyMoveStateOptions { ExpectedReplicasPerMaster = 0, EvictionPolicyAttested = true });
        return new DriverKit(kit, options ?? new StageDriverOptions { UpgradeHold = TimeSpan.Zero, AckStable = TimeSpan.Zero, DrainedSoak = TimeSpan.Zero });
    }

    public async Task<StateRecord> StateAsync() => (await Kit.Store.ReadAsync(Group, CancellationToken.None))!;

    // Init, a recent dry run and the consent to start, as an operator would have stored them.
    public async Task<StateRecord> PrepareAsync()
    {
        await Kit.InitAsync("a");
        var state = await StateAsync();
        var dry = new DryRunStamp(Kit.Time.GetUtcNow(), 1_000, "ok").Encode();
        await Kit.Scripts.FieldsAsync(Kit.StateKey, state.Id, [(StateFields.DryRun, dry)], []);
        (await Commands.ApproveAsync(Group, ApprovalKinds.Start, "release floor attested", "op", CancellationToken.None)).Should().Be(ApproveResult.Stored);
        return await StateAsync();
    }

    public async Task<StepOutcome?> StepAsync(TimeSpan? advance = null)
    {
        var outcome = await Driver.StepAsync(Group, CancellationToken.None);
        Kit.Time.Advance(advance ?? TimeSpan.FromSeconds(1));
        return outcome;
    }

    // Steps until the stored state is the one asked for and durable.
    public async Task<StateRecord> StepUntilAsync(string state, string direction = "fwd", int max = 60, TimeSpan? advance = null)
    {
        for (var step = 0; step < max; step++)
        {
            var current = await StateAsync();
            if (current.State == state && current.Direction == direction && current.Durable)
            {
                return current;
            }

            await StepAsync(advance);
        }

        throw new Xunit.Sdk.XunitException($"The group never reached {state} {direction}; it is at {(await StateAsync()).State}. Alerts: {string.Join(" ", Alerts.TakeLast(6).Select(a => $"{a.Code}{{{a.Detail}}}"))}");
    }

    public ValueTask DisposeAsync() => Kit.DisposeAsync();
}
