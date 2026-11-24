namespace UiPath.Caching.KeyMove;

/// <summary>What a registered cache stores under each key, which decides how the inventory asks a key for its length.</summary>
internal enum KeyMoveKind
{
    /// <summary>A string value, whose stored length is its <c>STRLEN</c>.</summary>
    String,

    /// <summary>A hash, whose length is its field count, its <c>HLEN</c>.</summary>
    Hash,
}
