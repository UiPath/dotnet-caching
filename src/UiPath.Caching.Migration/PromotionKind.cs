namespace UiPath.Caching.Migration;

/// <summary>How one run of the promotion ended.</summary>
internal enum PromotionKind
{
    /// <summary>Published, and L was deleted when N was confirmed.</summary>
    Promoted,

    /// <summary>The mover found N already published: L was deleted against its exact bytes so it drains, and nothing was published.</summary>
    Drained,

    /// <summary>The snapshot found no L.</summary>
    SourceAbsent,

    /// <summary>The capture saw N already there (read-through only); <c>Read</c> is the master read of N.</summary>
    NativeExists,

    /// <summary>The publish found N (read-through only); <c>Read</c> is N when it is found again, else the decoded L value uncached.</summary>
    PublishFoundNative,

    /// <summary>CAP answered that the identity changed; <c>Readmitted</c> tells whether the unit moved to the new one.</summary>
    IdentityAtCapture,

    /// <summary>PUB answered that the identity changed; <c>Readmitted</c> tells whether the unit moved to the new one.</summary>
    IdentityAtPublish,

    /// <summary>The fence moved and the retries are not used up: start again.</summary>
    Restart,

    /// <summary>The gate is closed or absent.</summary>
    GateClosed,

    /// <summary>The source read or decode was unreadable.</summary>
    Unreadable,

    /// <summary>The source could not be read now.</summary>
    Unavailable,

    /// <summary>The decoded value is logically expired.</summary>
    Expired,

    /// <summary>The decoded value is too large to promote.</summary>
    TooLarge,

    /// <summary>The decoded value is a cached null that the other side does not cache (the mover leaves it where it is).</summary>
    NullNotCached,

    /// <summary>The key does not map to the other side.</summary>
    Unmappable,

    /// <summary>The mover left a key whose remaining life is below its minimum.</summary>
    ShortTtl,

    /// <summary>Abandoned before publishing, <c>Reason</c> says why; the source is untouched.</summary>
    Abandoned,

    /// <summary>The fence moved again and again until the retries were used up.</summary>
    FenceRefused,

    /// <summary>The conservative deadline had passed (<c>promotion_expiring</c>).</summary>
    Expiring,

    /// <summary>The publish refused for another reason.</summary>
    Refused,

    /// <summary>The capture met a live supersession marker and the handler said to skip.</summary>
    Superseded,

    /// <summary>The capture met a live supersession marker and the handler said to try the key again.</summary>
    SupersededRetry,

    /// <summary>The handler said the identity changed; <c>Readmitted</c> tells whether the unit moved to the new one.</summary>
    SupersededRerun,

    /// <summary>The caller no longer owns the work (its lease lapsed or the state moved on) before a step that writes: nothing more was written and the key is left for the next owner.</summary>
    OwnershipLost,
}
