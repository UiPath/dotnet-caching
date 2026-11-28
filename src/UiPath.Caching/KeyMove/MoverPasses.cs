using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>Checks that every scan unit recorded a clean pass for the current identity, move and scan plan.</summary>
internal sealed class MoverPasses(IScanUnits units, IDatabase database, MoveKeyNames names, IGroupKeySpace space, Func<string, CancellationToken, Task<StateRecord?>> readState)
{
    public const string Forward = "fwd";

    public const string Reverse = "rev";

    /// <summary>Checks for a clean pass from every unit under the current acknowledged identity and direction.</summary>
    public async Task<bool> FinishedAfterAckAsync(string group, string direction, CancellationToken cancellationToken) =>
        await EvidenceAsync(group, direction, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Returns the identity, move, scan plan and server evidence for a clean pass, or null when the pass cannot be confirmed.</summary>
    public async Task<string?> EvidenceAsync(string group, string direction, CancellationToken cancellationToken)
    {
        try
        {
            return await EvaluateAsync(group, direction, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // A pass that cannot be read is no evidence that it finished.
            return null;
        }
    }

    private async Task<string?> EvaluateAsync(string group, string direction, CancellationToken cancellationToken)
    {
        if (direction is not (Forward or Reverse) || await readState(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return null;
        }

        if (state.State != KeyMoveStates.Moving || state.Direction != direction || !state.Durable || state.AllAck != state.Id || state.MoveId is not { } moveId)
        {
            return null;
        }

        var current = await units.ListAsync(cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
        {
            return null;
        }

        var side = KeyInventory.SourceOf(state);
        var scanPlan = MoveScan.HashOf(MoveScan.For(space, group, side));
        var evidence = new StringBuilder().Append(state.Id).Append('|').Append(moveId).Append('|').Append(scanPlan);
        foreach (var unit in current.OrderBy(u => u.Id, StringComparer.Ordinal))
        {
            var recorded = await database.StringGetAsync(names.UnitPass(group, unit.Id, direction, moveId), CommandFlags.DemandMaster).ConfigureAwait(false);
            if (!PassRecord.IsCleanFor((string?)recorded, state.Id, moveId, scanPlan))
            {
                return null;
            }

            // The live run id of the primary too: a restart since the record changes the evidence, and an unreadable one is no evidence.
            var runId = (await unit.Primary.InfoAsync("server").ConfigureAwait(false)).SelectMany(g => g).FirstOrDefault(e => e.Key == "run_id").Value;
            if (string.IsNullOrEmpty(runId))
            {
                return null;
            }

            evidence.Append('|').Append(unit.Id).Append('=').Append(PassRecord.SourceOf((string?)recorded)).Append('@').Append(runId);
        }

        return evidence.ToString();
    }
}
