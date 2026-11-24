namespace UiPath.Caching.Migration;

/// <summary>The pace and memory limits of the key-move mover, read live and checked by <see cref="KeyMoveMoverOptionsValidator"/>.</summary>
/// <remarks>
/// Internal: the mover is built inside the library; with no binding the defaults apply. Every rate, the cap of the node when it is set, has a floor
/// (<see cref="FloorKeysPerSecond"/> and <see cref="FloorBytesPerSecond"/>), so the longest single wait is about one key and one value (at most 256 KiB) at the floor: some 4 minutes.
/// </remarks>
internal sealed class KeyMoveMoverOptions
{
    /// <summary>The lowest rate the validator accepts, in keys per second: one key waits at most 10 s.</summary>
    public const double FloorKeysPerSecond = 0.1;

    /// <summary>The lowest rate the validator accepts, in bytes per second (1 KiB): a value of 256 KiB, the largest the mover moves, waits at most some 4 minutes.</summary>
    public const double FloorBytesPerSecond = 1024;

    /// <summary>The rate a scan unit starts at, in keys per second.</summary>
    public double StartKeysPerSecond { get; set; } = 100;

    /// <summary>The rate a scan unit starts at, in bytes per second.</summary>
    public double StartBytesPerSecond { get; set; } = 1024 * 1024;

    /// <summary>The floor a halving never goes under, in keys per second.</summary>
    public double MinKeysPerSecond { get; set; } = 1;

    /// <summary>The floor a halving never goes under, in bytes per second.</summary>
    public double MinBytesPerSecond { get; set; } = 16 * 1024;

    /// <summary>The ceiling an increase never goes over, in keys per second for one unit.</summary>
    public double MaxKeysPerSecond { get; set; } = 10_000;

    /// <summary>The ceiling an increase never goes over, in bytes per second for one unit.</summary>
    public double MaxBytesPerSecond { get; set; } = 256 * 1024 * 1024;

    /// <summary>The share of the rate added each <see cref="IncreaseInterval"/>; zero holds the rate.</summary>
    public double IncreaseStep { get; set; } = 0.10;

    public TimeSpan IncreaseInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the health and the memory rules are read; the rate is halved at most this often.</summary>
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a health source or a memory reading may take before it reads red.</summary>
    public TimeSpan HealthTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most keys per second all units of this node move together; zero or less is no cap.</summary>
    public double GlobalKeysPerSecond { get; set; }

    /// <summary>The most bytes per second all units of this node move together; zero or less is no cap.</summary>
    public double GlobalBytesPerSecond { get; set; }

    /// <summary>The windows (UTC) in which the mover may run; none means always.</summary>
    public List<MoveWindow> Windows { get; set; } = [];

    /// <summary>The share of <c>maxmemory</c> at which the mover pauses, above zero and at most one.</summary>
    public double MemoryCeilingFraction { get; set; } = 0.95;

    /// <summary>The window of the memory growth rule; at least twice <see cref="EvaluationInterval"/>.</summary>
    public TimeSpan MemoryGrowthWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The growth of <c>used_memory</c>, in bytes per second, above which the mover pauses; zero or less turns the rule off.</summary>
    public double MaxMemoryGrowthBytesPerSecond { get; set; } = 10 * 1024 * 1024;

    /// <summary>How long every unit of the node stops after an out-of-memory reply to a publish.</summary>
    public TimeSpan OomBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>CPU use of a Redis node, in percent of one core, at which the health turns amber.</summary>
    public double CpuAmberPercent { get; set; } = 70;

    public double CpuRedPercent { get; set; } = 90;

    /// <summary>Bytes a replica is behind its primary at which the health turns amber.</summary>
    public long ReplicationLagAmberBytes { get; set; } = 1024 * 1024;

    public long ReplicationLagRedBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>The p99 of the mover's time to move one key at which the health turns amber.</summary>
    public TimeSpan MoverP99Amber { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MoverP99Red { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The age after which a sample leaves the p99, so a pause that stops the samples still ends.</summary>
    public TimeSpan MoverP99Window { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeouts the mover met since the last health reading at which the health turns amber.</summary>
    public int TimeoutsAmber { get; set; } = 1;

    public int TimeoutsRed { get; set; } = 5;
}
