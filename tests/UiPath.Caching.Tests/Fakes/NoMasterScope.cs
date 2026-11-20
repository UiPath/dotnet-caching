namespace UiPath.Caching.Tests.Fakes;

internal sealed class NoMasterScope : IDisposable
{
    public static NoMasterScope Instance { get; } = new();

    public void Dispose()
    {
    }
}
