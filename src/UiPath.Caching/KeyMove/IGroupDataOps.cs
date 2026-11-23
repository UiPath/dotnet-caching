namespace UiPath.Caching.KeyMove;

internal interface IGroupDataOps
{
    /// <summary>Counts owned native keys on the masters.</summary>
    Task<long> CountNativeAsync(string group, CancellationToken cancellationToken);

    /// <summary>Deletes every owned N key with a master confirming pass, checking the guard again through the sweep.</summary>
    Task PurgeNativeAsync(string group, DataGuard guard, CancellationToken cancellationToken);

    /// <summary>Deletes the L residue with a master confirming pass (X11).</summary>
    Task PurgeLegacyAsync(string group, DataGuard guard, CancellationToken cancellationToken);
}
