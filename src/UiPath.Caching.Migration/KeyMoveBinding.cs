namespace UiPath.Caching.Migration;

/// <summary>What a router cache needs besides its key move traits to run the key move forms: the codec of each side and the key maps.</summary>
internal sealed record KeyMoveBinding<T>(IKeyMoveCodec<T> InPlace, IKeyMoveCodec<T> Native, Func<string, string> InPlaceKey, Func<string, string> NativeKey, bool NoOrigin)
    where T : class
{
    public KeyMoveOptions Options { get; init; } = new();
}
