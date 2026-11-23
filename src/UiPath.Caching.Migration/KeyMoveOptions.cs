namespace UiPath.Caching.Migration;

internal sealed record KeyMoveOptions
{
    /// <summary>How long a promotion holds work.</summary>
    public TimeSpan PromotionBudget { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan CaptureToSnapshotMax { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan FenceTtl { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan HintTtl { get; init; } = TimeSpan.FromSeconds(5);

    public int MaxHints { get; init; } = 4096;

    /// <summary>How often a unit re-runs under a new identity before the call gives up.</summary>
    public int MaxReruns { get; init; } = 4;

    /// <summary>If the fence moves, promotion can retry from the destination replica read up to this many times.</summary>
    public int MaxFenceRetries { get; init; } = 2;

    public TimeSpan RetryInitial { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The most trailing steps held for retry; one more gives up at once.</summary>
    public int RetryCapacity { get; init; } = 4096;

    /// <summary>How long a failed trailing step is retried when its write has no expiry.</summary>
    public TimeSpan RetryLife { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan AdmissionPoll { get; init; } = TimeSpan.FromMilliseconds(50);
}
