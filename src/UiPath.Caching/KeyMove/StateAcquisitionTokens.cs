using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// TRANSIT clears the token and uncertainty fields together when it binds the claim.
internal sealed class StateAcquisitionTokens(MoveScripts scripts, IDatabase dst, Func<string, RedisKey> stateKey) : IAcquisitionTokens, IAcquisitionGuard
{
    public async Task<AcquisitionHold> HoldsAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) =>
        await scripts.AcquisitionAsync(stateKey(group), token, fence).WaitAsync(cancellationToken).ConfigureAwait(false) switch
        {
            1 => AcquisitionHold.Owned,
            0 => AcquisitionHold.None,
            MoveScripts.AcquisitionOther => AcquisitionHold.Other,
            _ => AcquisitionHold.LeaseNotHeld,
        };

    public async Task<AcquisitionToken?> ReadAsync(string group, CancellationToken cancellationToken)
    {
        var held = await dst.HashGetAsync(stateKey(group), [StateFields.PendToken, StateFields.PendUncertain], CommandFlags.DemandMaster).WaitAsync(cancellationToken).ConfigureAwait(false);
        var stored = (string?)held[0];
        return stored is { Length: 32 } && stored.All(char.IsAsciiHexDigit) ? new AcquisitionToken(stored, held[1] == "1") : null;
    }

    public Task<bool> WriteAsync(string group, string token, LeaseFence? fence, CancellationToken cancellationToken) =>
        SetAsync(group, [(StateFields.PendToken, token)], fence, cancellationToken);

    public Task<bool> MarkSrcUncertainAsync(string group, LeaseFence? fence, CancellationToken cancellationToken) =>
        SetAsync(group, [(StateFields.PendUncertain, "1")], fence, cancellationToken);

    public async Task<bool> ClearAsync(string group, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            // 0 is no state hash: there is nothing stored to clear.
            return await scripts.FieldsAsync(stateKey(group), string.Empty, [], [StateFields.PendToken, StateFields.PendUncertain], fence).WaitAsync(cancellationToken).ConfigureAwait(false) is MoveScripts.Written or 0;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> SetAsync(string group, IReadOnlyList<(string Field, string Value)> fields, LeaseFence? fence, CancellationToken cancellationToken)
    {
        try
        {
            return await scripts.FieldsAsync(stateKey(group), string.Empty, fields, [], fence).WaitAsync(cancellationToken).ConfigureAwait(false) == MoveScripts.Written;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return false;
        }
    }
}
