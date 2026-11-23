namespace UiPath.Caching.KeyMove;

/// <summary>The two key formats of a move.</summary>
internal enum MoveSide
{
    /// <summary>N: the native keys of the library.</summary>
    Native,

    /// <summary>L: the in-place keys of the app.</summary>
    Legacy,
}
