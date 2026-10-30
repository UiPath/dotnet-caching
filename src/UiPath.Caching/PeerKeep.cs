namespace UiPath.Caching;

/// <summary>What keeping a refresh read did to the held entry.</summary>
internal enum PeerKeep
{
    /// <summary>The read replaced the held entry.</summary>
    Kept,

    /// <summary>A newer local write, a removal or a loss replaced or dropped the held entry first.</summary>
    Replaced,

    /// <summary>A change since the read began kept it out while the entry is still held.</summary>
    Declined,

    /// <summary>The local write failed, and the key was removed.</summary>
    Failed,
}
