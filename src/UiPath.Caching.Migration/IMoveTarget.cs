namespace UiPath.Caching.Migration;

/// <summary>One managed cache as the mover moves it: the same promotion as a read-through, for a key found by scan.</summary>
internal interface IMoveTarget
{
    string CacheId { get; }

    /// <summary>The cache declares <c>NoOrigin</c>, so <c>MoveMinTtl</c> never applies to it.</summary>
    bool NoOrigin { get; }

    /// <param name="minTtl">The remaining life below which a key is left alone; zero moves every key.</param>
    /// <param name="identity">The state identity the runner holds; a key is refused with <c>identity</c> when the admitted unit runs under another.</param>
    /// <param name="ownership">Asked before every step of the promotion that writes and before every retry; a key is left for the next owner, uncounted, when it says no.</param>
    ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken);

    /// <summary>Reads what the mover would find at <paramref name="key"/> and writes nothing: no fence, no publish, no delete, no counter.</summary>
    /// <param name="decode">Decode the value to judge it and measure its encoding; false classifies by the remaining life alone.</param>
    ValueTask<DryRunProbe> ProbeAsync(string key, TimeSpan minTtl, bool decode, CancellationToken cancellationToken);
}
