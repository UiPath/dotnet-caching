namespace UiPath.Caching.KeyMove;

/// <summary>The rule for a key that cannot have been written before moving.</summary>
internal static class S3Canary
{
    /// <summary>True when the remaining life plus the time since moving exceeds the longest life a source key can have.</summary>
    public static bool WrittenAfterS3(double lifeMs, long now, long s3At, TimeSpan maxSourceTtl) => lifeMs + (now - s3At) > maxSourceTtl.TotalMilliseconds;
}
