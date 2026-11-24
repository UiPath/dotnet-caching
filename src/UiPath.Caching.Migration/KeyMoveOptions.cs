namespace UiPath.Caching.Migration;

internal sealed record KeyMoveOptions
{
    /// <summary>The largest stored value that is promoted; a larger one is served from where it is, and dropped for a cache that has an origin.</summary>
    public int MaxValueBytes { get; init; } = 4 * 1024 * 1024;

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

    /// <summary>The longest a marked set may wait before the script refuses it: its deadline on the server's clock, counted from the client's.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The bound on the difference of the clocks of client and server; a permit outlives a deadline by it.</summary>
    public TimeSpan SkewBound { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The longest a permit is kept for a transaction with no reply when its connection has not changed.</summary>
    public TimeSpan FallbackPermitHold { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The longest in-place key a marked write takes; a longer one fails closed before any step.</summary>
    public int MarkerKeyMax { get; init; } = 256;

    /// <summary>The forward marked writes a node has outstanding; more wait for a permit.</summary>
    public int NoOriginWriteInflightMax { get; init; } = 256;

    /// <summary>The unmarks a node holds; one more is dropped and counted.</summary>
    public int UnmarkQueueMax { get; init; } = 10_000;

    public int UnmarkBatch { get; init; } = 500;

    public int UnmarkAttempts { get; init; } = 5;

    public TimeSpan UnmarkFlushInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How often a read or create that meets a live marker helps and runs again before it gives up as unavailable.</summary>
    public int MaxMarkerHelps { get; init; } = 3;
}
