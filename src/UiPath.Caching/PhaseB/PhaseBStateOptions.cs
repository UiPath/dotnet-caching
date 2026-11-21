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

    /// <summary>The age beyond which a snapshot is stale: <see cref="MaxSnapshotAge"/>, or three polls when that is longer.</summary>
    public TimeSpan StaleAfter(TimeSpan pollInterval) => TimeSpan.FromTicks(Math.Max(MaxSnapshotAge.Ticks, pollInterval.Ticks * 3));
}
