namespace UiPath.Caching.Migration;

/// <summary>Where a hash promotion ended.</summary>
internal enum HashPromotionStatus
{
    /// <summary>N holds the whole hash or the key is nowhere, and L holds nothing of it.</summary>
    Complete,

    /// <summary>L still holds the hash after the unit: it changed under the promotion, N was gone, or the promotion did not finish.</summary>
    Changing,

    /// <summary>L is above the field or the byte cap and was not promoted; it was dropped when the cache has an origin.</summary>
    TooLarge,

    /// <summary>L cannot be read, or holds a field name the in-place format cannot map.</summary>
    Unreadable,

    /// <summary>The promotion was abandoned and the hash served uncached: a slow capture, an exhausted budget, an expiring source, a refused fence or a closed gate.</summary>
    NotPromoted,

    /// <summary>The identity changed: the caller plans again under the rules now in force.</summary>
    Replan,

    /// <summary>A command could not be sent or answered.</summary>
    Unavailable,

    /// <summary>The fence moved: the promotion starts again from the top. Never returned by <see cref="HashPromotion{T}.PromoteAsync"/>.</summary>
    Restart,
}
