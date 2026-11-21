namespace UiPath.Caching.PhaseB;

/// <summary>The census and the deletes of owned N and L keys; the real implementation is <c>PurgeData</c> of the recovery work.</summary>
internal interface IGroupDataOps
{
    /// <summary>G14 and the X3 census: owned N keys found by a master census.</summary>
    Task<long> CountNativeAsync(string group, CancellationToken cancellationToken);

    /// <summary>Deletes every owned N key with a master confirming pass.</summary>
    Task PurgeNativeAsync(string group, CancellationToken cancellationToken);

    /// <summary>Deletes the L residue with a master confirming pass (X11).</summary>
    Task PurgeLegacyAsync(string group, CancellationToken cancellationToken);
}
