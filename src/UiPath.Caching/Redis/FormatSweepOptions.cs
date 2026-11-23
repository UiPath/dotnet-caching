namespace UiPath.Caching.Redis;

/// <summary>The limits of one pass of <see cref="RedisCacheProvider.SweepFormatsAsync{T}"/>.</summary>
public sealed class FormatSweepOptions
{
    /// <summary>The most keys of another format a pass takes in full each second, each costing a length check, a read and a compare-and-set; 50 by default. A key already in the target format, offered lately, or over the size limit costs its checks and is not counted against this.</summary>
    public int RatePerSecond { get; init; } = 50;

    /// <summary>The most a value may weigh to be rewritten, 256 KiB by default; a bigger one is left and reported as oversize. A rewrite never carries more than 256 KiB, so a higher setting is capped to that.</summary>
    public int MaxValueBytes { get; init; } = 256 * 1024;

    /// <summary>How long a pass runs; it stops before the next key it would take in full once this has passed, so it overruns by at most one key's turn, and the cursor it gives starts the page again. A pass always takes one key in full. Null runs until the keys are done.</summary>
    public TimeSpan? Budget { get; init; }

    /// <summary>The SCAN <c>COUNT</c> hint, the keys looked at a page; 500 by default.</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>The <see cref="FormatSweepReport.NextCursor"/> of an earlier pass to continue from; null starts at the beginning, and so does a cursor of a server that is no longer the one it named.</summary>
    public string? Cursor { get; init; }

    /// <summary>The <c>group</c> tag of the sweep's metrics, so a pass over one set of keys is told from another; <c>default</c> if not set.</summary>
    public string Group { get; init; } = "default";
}
