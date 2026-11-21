namespace UiPath.Caching.Redis;

/// <summary>The limits of one pass of <see cref="RedisCacheProvider.SweepFormatsAsync{T}"/>.</summary>
public sealed class FormatSweepOptions
{
    /// <summary>The most keys of another format a pass takes in full each second, each costing a length check, a read and a compare-and-set; 50 by default. A key already in the target format costs one two-byte read and is not counted against this.</summary>
    public int RatePerSecond { get; init; } = 50;

    /// <summary>The most a value may weigh to be rewritten, 256 KiB by default; a bigger one is left and reported as oversize. A rewrite never carries more than 256 KiB, so a higher setting is capped to that.</summary>
    public int MaxValueBytes { get; init; } = 256 * 1024;

    /// <summary>How long a pass runs; it stops between two SCAN pages once this has passed, so it can overrun by one page. Null runs until the keys are done.</summary>
    public TimeSpan? Budget { get; init; }

    /// <summary>The SCAN <c>COUNT</c> hint, the keys looked at a page; 500 by default.</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>The <see cref="FormatSweepReport.NextCursor"/> of an earlier pass to continue from; null starts at the beginning.</summary>
    public string? Cursor { get; init; }

    /// <summary>The <c>group</c> tag of the sweep's metrics, so a pass over one set of keys is told from another; <c>default</c> if not set.</summary>
    public string Group { get; init; } = "default";
}
