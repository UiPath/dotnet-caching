namespace UiPath.Caching.Migration;

/// <summary>Where a hash promotion ended.</summary>
internal enum HashPromotionStatus
{
    /// <summary>The target can answer the read, or the source has no hash left to move. The target is N forward and L in reverse.</summary>
    Complete,

    /// <summary>The source still holds the hash because it changed during promotion, the target disappeared, or promotion did not finish.</summary>
    Changing,

    /// <summary>The source exceeds the field or byte cap.</summary>
    TooLarge,

    /// <summary>The source expires before the mover's minimum remaining life.</summary>
    ShortTtl,

    /// <summary>L cannot be read, or holds a field name the in-place format cannot map.</summary>
    Unreadable,

    /// <summary>The destination holds a value of a different Redis type.</summary>
    WrongDestinationType,

    /// <summary>The promotion was abandoned and the hash served uncached: a slow capture, an exhausted budget, an expiring source, a refused fence or a closed gate.</summary>
    NotPromoted,

    /// <summary>The identity changed: the caller plans again under the rules now in force.</summary>
    Replan,

    /// <summary>A command could not be sent or answered.</summary>
    Unavailable,

    /// <summary>The fence moved: the promotion starts again from the top. Never returned by <see cref="HashPromotion{T}.PromoteAsync"/>.</summary>
    Restart,
}
