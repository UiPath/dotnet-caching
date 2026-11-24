namespace UiPath.Caching.Migration;

internal enum DecodeClass
{
    Ok,
    Unreadable,

    /// <summary>A cached null: no value, but the entry exists.</summary>
    Null,

    /// <summary>Not promoted: it is served from where it is.</summary>
    TooLarge,

    /// <summary>The entry's own deadline has passed: a miss.</summary>
    Expired,
}
