namespace UiPath.Caching.PhaseB;

internal sealed class PhaseBStateOptions
{
    public int StateWaitReplicas { get; init; } = 1;

    /// <summary>How many times a barrier is tried in all, each on a replaced connection after the first.</summary>
    public int BarrierRetries { get; init; } = 3;

    public TimeSpan BarrierWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Replicas configured for each master when the topology does not say; null reads them from the topology.</summary>
    public int? ExpectedReplicasPerMaster { get; init; }

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan SlowPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxSnapshotAge { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan StartupWait { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan TransitionWait { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LeaseRenewInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan UpkeepInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan ClearLockTtl { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan ClearLockRenewInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a Clear closes the gates and sweeps again when a transition replaced the gates during the sweep.</summary>
    public int ClearRetries { get; init; } = 3;

    /// <summary>A gate closed for longer than this raises <c>gate_stuck</c>.</summary>
    public TimeSpan GateStuckAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a master that failed a barrier counts as failing for the way back.</summary>
    public TimeSpan BarrierFailureWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The operator states in configuration that the eviction policy is <c>noeviction</c> or a <c>volatile-*</c> one.</summary>
    public bool EvictionPolicyAttested { get; init; }

    /// <summary>The age beyond which a snapshot is stale: <see cref="MaxSnapshotAge"/>, or three polls when that is longer.</summary>
    public TimeSpan StaleAfter(TimeSpan pollInterval) => TimeSpan.FromTicks(Math.Max(MaxSnapshotAge.Ticks, pollInterval.Ticks * 3));
}
