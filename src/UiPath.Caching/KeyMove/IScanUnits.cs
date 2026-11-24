namespace UiPath.Caching.KeyMove;

/// <summary>The scan units of the connection as they are now.</summary>
internal interface IScanUnits
{
    Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken);
}
