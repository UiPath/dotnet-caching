namespace UiPath.Caching.Migration;

/// <summary>One admitted operation: stop waits for every admission to be disposed before it flushes and disposes anything.</summary>
internal readonly struct Admission(RouterCore core) : IDisposable
{
    public void Dispose() => core.Exit();
}
