namespace UiPath.Caching.Migration;

/// <summary>What a router hash cache needs besides its key move traits to run the key move forms: the codec of each side and the key maps.</summary>
/// <param name="MaxHashFields">The most fields a hash may have to be promoted, set per registration.</param>
internal sealed record KeyMoveHashBinding<T>(IKeyMoveHashCodec<T> InPlace, IKeyMoveHashCodec<T> Native, Func<string, string> InPlaceKey, Func<string, string> NativeKey, bool NoOrigin, int MaxHashFields)
    where T : class
{
    public KeyMoveOptions Options { get; init; } = new();
}
