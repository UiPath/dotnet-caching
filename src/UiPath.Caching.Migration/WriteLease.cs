namespace UiPath.Caching.Migration;

/// <summary>The policy of one write, holding its group in the gate's coverage until the write has ended.</summary>
internal readonly struct WriteLease(CachePolicy? policy, GroupPin? writing) : IDisposable
{
    public CachePolicy? Policy { get; } = policy;

    public void Dispose() => writing?.ExitWriting();
}
