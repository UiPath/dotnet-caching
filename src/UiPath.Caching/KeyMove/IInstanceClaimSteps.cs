namespace UiPath.Caching.KeyMove;

internal interface IInstanceClaimSteps : IMoveClaim
{
    Task<ClaimOutcome> CloseAsync(string group, string token, CancellationToken cancellationToken);
}
