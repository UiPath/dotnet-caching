using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary><c>RemoveGroup</c>, the one command that deletes control records and the state, for an S0 group or an S5 group whose sweep completed.</summary>
/// <remarks>The claim field goes first, the records next and the state last, so a command that stopped half way is repeated from the state it left.</remarks>
/// <param name="dropClaim">CLAIMDROP of the move claim, which another change owns; null when the group takes no claim.</param>
internal sealed class GroupRemoval(StateStore store, MoveScripts stateScripts, MoveScripts controlScripts, MoveKeyNames names, Func<string, LeaseFence?, CancellationToken, Task<ClaimOutcome>>? dropClaim = null)
{
    private const int Batch = 512;

    public async Task<RemoveOutcome> RemoveGroupAsync(string group, LeaseFence? fence, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return RemoveOutcome.NoState;
        }

        var terminal = state.State == "S0" ? state.Field(StateFields.ColdStart).Length == 0 : state.State == "S5" && state.Swept == state.Id;
        if (!state.Durable || !terminal)
        {
            return RemoveOutcome.NotApplicable;
        }

        // Nothing is touched under a lease that is gone or a state that moved since it was read.
        if (await stateScripts.GuardAsync(names.State(group), state.Id, fence).ConfigureAwait(false) != MoveScripts.Written)
        {
            return RemoveOutcome.Refused;
        }

        if (state.Claim.Length > 0)
        {
            if (dropClaim is null)
            {
                return RemoveOutcome.ClaimHeld;
            }

            if (await dropClaim(group, fence, cancellationToken).ConfigureAwait(false) != ClaimOutcome.Done)
            {
                return RemoveOutcome.ClaimDropFailed;
            }
        }

        var remaining = 0;
        foreach (var chunk in names.SlotSet().Chunk(Batch))
        {
            var deletes = await Task.WhenAll(chunk.Select(slot => DeleteRecordAsync(group, slot, state.Ep))).ConfigureAwait(false);
            remaining += deletes.Count(d => d == MoveScripts.IdentityMismatch);
        }

        if (remaining > 0)
        {
            return RemoveOutcome.RecordsRemain;
        }

        var key = names.State(group);
        return await stateScripts.GuardedDeleteAsync(key, key, DeleteGuard.Identity, state.Id, fence).ConfigureAwait(false) switch
        {
            MoveScripts.IdentityMismatch or MoveScripts.LeaseNotHeld => RemoveOutcome.Refused,
            _ => RemoveOutcome.Removed,
        };
    }

    private Task<long> DeleteRecordAsync(string group, int slot, long ep)
    {
        var record = names.Control(group, slot);
        return controlScripts.GuardedDeleteAsync(record, record, DeleteGuard.EpochAtMost, ep.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
