namespace UiPath.Caching.KeyMove;

internal interface IAcquisitionTokens
{
    Task<AcquisitionToken?> ReadAsync(string group, CancellationToken cancellationToken);

    Task<bool> WriteAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken);

    Task<bool> MarkSrcUncertainAsync(string group, LeaseFence? fence, CancellationToken cancellationToken);

    Task<bool> ClearAsync(string group, LeaseFence? fence, CancellationToken cancellationToken);
}
