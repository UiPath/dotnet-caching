using System.Text;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>The drain check as the mover reports it: every current scan unit recorded a clean pass under the current identity and move id.</summary>
internal sealed class MoverPasses(IScanUnits units, IDatabase database, MoveKeyNames names, Func<string, CancellationToken, Task<StateRecord?>> readState, Func<string, bool>? hasHashCache = null, Action<KeyMoveAlert>? alert = null)
{
    public const string Forward = "fwd";

    public const string Reverse = "rev";

    /// <summary>The alert code and reason of the one check that fails the drain check for a group with a hash cache.</summary>
    public const string NoHashPromotion = "no_hash_promotion";

    /// <summary>True only for an S3 of the given direction with <c>dur=1</c> and <c>allAck</c> equal to its id, whose units all recorded a clean pass under that identity and move id.</summary>
    public async Task<bool> FinishedAfterAckAsync(string group, string direction, CancellationToken cancellationToken) =>
        await EvidenceAsync(group, direction, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Null unless <see cref="FinishedAfterAckAsync"/> holds; then the evidence the passes rest on: the identity, the move id, and each unit with the server its pass scanned. It changes when the identity or the topology (the primaries, and the run id of each) does.</summary>
    public async Task<string?> EvidenceAsync(string group, string direction, CancellationToken cancellationToken)
    {
        if (HashPromotionMissing(group))
        {
            return null;
        }

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

    /// <summary>True, with an alert, for a group with a hash cache, which the mover cannot move yet; hash promotion lifts the drain check's refusal here and nowhere else.</summary>
    private bool HashPromotionMissing(string group)
    {
        if (hasHashCache?.Invoke(group) != true)
        {
            return false;
        }

        alert?.Invoke(new KeyMoveAlert(group, NoHashPromotion, "the mover does not promote hash keys yet, so a finished pass is no proof that the group is drained"));
        return true;
    }

    private async Task<string?> EvaluateAsync(string group, string direction, CancellationToken cancellationToken)
    {
        if (direction is not (Forward or Reverse) || await readState(group, cancellationToken).ConfigureAwait(false) is not { } state)
        {
            return null;
        }

        if (state.State != "S3" || state.Direction != direction || !state.Durable || state.AllAck != state.Id || state.MoveId is not { } moveId)
        {
            return null;
        }

        var current = await units.ListAsync(cancellationToken).ConfigureAwait(false);
        if (current.Count == 0)
        {
            return null;
        }

        var evidence = new StringBuilder().Append(state.Id).Append('|').Append(moveId);
        foreach (var unit in current.OrderBy(u => u.Id, StringComparer.Ordinal))
        {
            var recorded = await database.StringGetAsync(names.UnitPass(group, unit.Id, direction, moveId), CommandFlags.DemandMaster).ConfigureAwait(false);
            if (!PassRecord.IsCleanFor((string?)recorded, state.Id, moveId))
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
