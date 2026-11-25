namespace UiPath.Caching.Migration;

/// <summary>The classes a dry run sorts keys into.</summary>
internal static class DryRunClasses
{
    public const string Hash = "hash";
    public const string Movable = "movable";
    public const string Expired = "expired";
    public const string ShortTtl = "short_ttl";
    public const string TooLarge = "too_large";
    public const string NullNotCached = "null_not_cached";
    public const string Unreadable = "unreadable";
    public const string Unmappable = "unmappable";
    public const string Duplicate = "duplicate";
    public const string Absent = "absent";
    public const string Unavailable = "unavailable";
    public const string ExpectedUnmapped = "expected_unmapped";
    public const string Ambiguous = "ambiguous";
    public const string NoTarget = "no_target";
}
