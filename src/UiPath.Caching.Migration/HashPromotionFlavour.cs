namespace UiPath.Caching.Migration;

/// <summary>Why a hash is promoted, which decides what the caller is handed back.</summary>
internal enum HashPromotionFlavour
{
    /// <summary>A read-through: the caller wants the hash, and gets it from N when it is there.</summary>
    Read,

    /// <summary>A field merge or a mover pass: the caller wants L gone, and gets the status alone.</summary>
    Ensure,
}
