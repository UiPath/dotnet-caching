namespace UiPath.Caching.Migration;

/// <summary>The policy of one write, holding its group in the gate's coverage until the write has ended.</summary>
internal readonly struct WriteLease(CachePolicy? policy, GroupPin? writing) : IDisposable
{
    public CachePolicy? Policy { get; } = policy;

    /// <summary>Takes another hold for a queued write that uses the same encoded bytes.</summary>
    public WriteLease Retain()
    {
        writing?.EnterWriting();
        return this;
    }

    public void Dispose() => writing?.ExitWriting();
}
