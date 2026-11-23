namespace UiPath.Caching.KeyMove;

internal interface IAcquisitionGuard
{
    Task<AcquisitionHold> HoldsAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken);
}
