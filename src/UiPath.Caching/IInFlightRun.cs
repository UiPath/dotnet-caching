namespace UiPath.Caching;

/// <summary>The shared run as its work sees it: the token it runs on, and permission to commit a side effect only while a caller still waits.</summary>
internal interface IInFlightRun
{
    CancellationToken Token { get; }

    /// <summary>Runs <paramref name="commit"/> unless every caller has left; false when it did not.</summary>
    bool TryCommit<TArg>(TArg arg, Action<TArg> commit);
}
