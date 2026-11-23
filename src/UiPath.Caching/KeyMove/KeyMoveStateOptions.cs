namespace UiPath.Caching.KeyMove;

internal sealed class KeyMoveStateOptions
{
    private readonly int _stateWaitReplicas = 1;

    /// <summary>1 to wait for a replica at every barrier, 0 for none; the server barrier waits for exactly one replica, so no other count is accepted.</summary>
    public int StateWaitReplicas
    {
        get => _stateWaitReplicas;
        init => _stateWaitReplicas = value is 0 or 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(StateWaitReplicas), value, "The state barrier waits for one replica (WAIT 1); StateWaitReplicas can only be 0 or 1.");
    }

    /// <summary>How many times a barrier is tried in all, each on a replaced connection after the first.</summary>
    public int BarrierRetries { get; init; } = 3;

    public TimeSpan BarrierWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Replicas configured for each master when the topology does not say; null reads them from the topology.</summary>
    public int? ExpectedReplicasPerMaster { get; init; }

    /// <summary>The bound of the Dst health read that runs immediately before the first Src write of a transition (<c>dst_preflight</c>); a read that exceeds it fails the step, which waits.</summary>
    public TimeSpan DstPreflightTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a pending claim whose token the state does not show (<c>ct</c>) stays unchanged before the leader releases it to idle (the leader's monotonic clock).</summary>
    public TimeSpan ClaimPendHold { get; init; } = TimeSpan.FromSeconds(60);

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
