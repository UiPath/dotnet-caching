namespace UiPath.Caching.KeyMove;

/// <summary>What a refresh does to the metadata field of a hash.</summary>
internal enum HashMetadataClear
{
    /// <summary>The metadata stays: an expiry-only refresh.</summary>
    Keep,

    /// <summary>The field is deleted.</summary>
    Remove,

    /// <summary>The field is set to the empty marker, as a library that caches nulls clears metadata.</summary>
    Mark,
}
