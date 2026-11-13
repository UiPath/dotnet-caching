namespace UiPath.Caching.Serialization;

internal enum RewriteOutcome
{
    Done,
    Changed,
    Lossy,
    Held,
    RateLimited,
    Dropped,
    Failed,
}
