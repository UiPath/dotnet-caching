namespace UiPath.Platform.Caching.Locking;

[ExcludeFromCodeCoverage]
internal sealed class NoOpAsyncDisposable : IAsyncDisposable
{
    public static readonly NoOpAsyncDisposable Instance = new();

    public ValueTask DisposeAsync() => default;
}
