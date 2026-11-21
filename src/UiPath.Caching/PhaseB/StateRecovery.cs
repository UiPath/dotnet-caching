using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>Recreation of a flushed state from fresh peers and the operator's <c>Reassert</c>; both write the state with the create-if-absent or replacing TRANSIT and run the standard sequence.</summary>
internal sealed class StateRecovery(StateStore store, ControlSampler sampler, ControlRecordReader records, IDatabase controlDatabase, TransitionSequence sequence, MoveKeyNames names, TimeProvider time)
{
    private static readonly string[] AlwaysCleared = [StateFields.AllAck, StateFields.Swept, StateFields.DrainedAt, StateFields.ClosedAt, StateFields.DryRun, StateFields.HoldAuto];

    /// <param name="registeredCaches">The caches the nodes registered (<c>ca</c>), used only for S0; S1 to S5 take the set from the surviving records.</param>
    public async Task<RecoveryResult> RecreateAsync(string group, IReadOnlyList<PeerIdentity> peers, IReadOnlyList<string> registeredCaches, ClaimFields? claim, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(group, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new RecoveryResult(RecoveryOutcome.StateExists);
        }

        if (peers.Count == 0)
        {
            return new RecoveryResult(RecoveryOutcome.NoPeers);
        }

        var identity = peers[0];
        if (peers.Any(p => p != identity))
        {
            return new RecoveryResult(RecoveryOutcome.PeersDisagree);
        }

        var sample = await sampler.SampleAsync(group, cancellationToken).ConfigureAwait(false);
        if (sample is not null && (sample.Ep != identity.Ep || sample.Id != identity.Id || sample.State != identity.State || sample.Direction != identity.Direction))
        {
            return new RecoveryResult(RecoveryOutcome.RecordsDisagree);
        }

        string caches;
        if (identity.State == "S0")
        {
            caches = StateRecord.EncodeCaches(registeredCaches);
        }
        else if (sample is null)
        {
            return new RecoveryResult(RecoveryOutcome.NeedsReassert);
        }
        else
        {
            var gates = await controlDatabase.HashKeysAsync(names.Control(group, sample.Slot), CommandFlags.DemandMaster).ConfigureAwait(false);
            caches = StateRecord.EncodeCaches(gates.Select(g => (string)g!).Where(g => g.StartsWith("c:", StringComparison.Ordinal)).Select(g => g[2..]));
        }

        var newEp = Math.Max(identity.Ep, sample?.Ep ?? 0) + 1;
        var target = new StateTarget(
            identity.State,
            identity.Direction,
            DerivedNPop(identity.State, identity.Direction),
            newEp,
            MoveScripts.NewNonce(),
            StateRecord.FormatPrev(identity.State, identity.Direction, DerivedNPop(identity.State, identity.Direction), identity.Ep, identity.Id),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: newEp,
            Claim: claim?.Token,
            Caches: (caches, StateRecord.HashOfCaches(caches)),
            Move: claim?.Move,
            Release: claim?.Release,
            HoldOp: "recreated");
        var result = await sequence.RunAsync(group, StateExpectation.Absent, target, null, cancellationToken).ConfigureAwait(false);
        return new RecoveryResult(RecoveryOutcome.Done, result);
    }

    /// <summary>
    /// Rebuilds the state from the control records of every slot. With no record, the operator names the state and the managed set.
    /// The new state keeps no residue counts, so the source fallback stays on, and carries <c>hold:op</c>.
    /// </summary>
    public async Task<RecoveryResult> ReassertAsync(string group, string confirm, (string State, string Direction)? named, IReadOnlyList<string>? caches, ClaimFields? claim, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirm);
        var found = (await records.ReadAllAsync(group, cancellationToken).ConfigureAwait(false)).Where(r => r.Fields.ContainsKey(StateFields.Ep)).Select(r => r.Fields).ToArray();
        var stored = await store.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        string state;
        string direction;
        int nPop;
        string encoded;
        long highest;
        long committedEp;
        string committedId;
        if (found.Length > 0)
        {
            highest = found.Max(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture));
            var committed = found.Where(r => r.GetValueOrDefault(StateFields.Durable) == "1").ToArray();
            if (committed.Length > 0)
            {
                committedEp = committed.Max(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture));
                var atEp = committed.Where(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == committedEp).ToArray();
                if (atEp.Select(r => (r[StateFields.State], r[StateFields.Direction])).Distinct().Count() > 1)
                {
                    return new RecoveryResult(RecoveryOutcome.RecordsDisagreeOnDirection);
                }

                (state, direction, nPop, committedId) = (atEp[0][StateFields.State], atEp[0][StateFields.Direction], ParseNPop(atEp[0], "np"), atEp[0][StateFields.Id]);
            }
            else
            {
                // The highest transition never ran on any slot: the state is the one it started from.
                var top = found.First(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == highest);
                (state, direction, nPop, committedId) = (top["pst"], top["pdir"], ParseNPop(top, "pnp"), top["pid"]);
                committedEp = long.Parse(top["pep"], CultureInfo.InvariantCulture);
            }

            var gates = found.First(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == highest).Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).Select(k => k[2..]);
            encoded = StateRecord.EncodeCaches(gates);
        }
        else
        {
            if (named is null || caches is null)
            {
                return new RecoveryResult(RecoveryOutcome.NoRecords);
            }

            (state, direction) = named.Value;
            nPop = DerivedNPop(state, direction);
            encoded = StateRecord.EncodeCaches(caches);
            highest = Math.Max(stored?.Ep ?? 0, await store.ServerMicrosecondsAsync(group).ConfigureAwait(false));
            committedEp = highest;
            committedId = "0";
        }

        var newEp = Math.Max(highest, stored?.Ep ?? 0) + 1;
        var clear = stored?.FieldNames?
            .Where(f => f.StartsWith(StateFields.ResiduePrefix, StringComparison.Ordinal) || f.StartsWith(StateFields.ApprovalPrefix, StringComparison.Ordinal) || AlwaysCleared.Contains(f))
            .ToArray() ?? [];
        var target = new StateTarget(
            state,
            direction,
            nPop,
            newEp,
            MoveScripts.NewNonce(),
            StateRecord.FormatPrev(state, direction, nPop, committedEp, committedId),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: newEp,
            Claim: claim?.Token,
            Caches: (encoded, StateRecord.HashOfCaches(encoded)),
            Move: claim?.Move,
            Release: claim?.Release,
            HoldOp: "reassert",
            Clear: clear);
        var expected = stored is null ? StateExpectation.Absent : StateExpectation.Holding(stored.Ep, stored.Id, stored.Durable ? 1 : 0);
        return new RecoveryResult(RecoveryOutcome.Done, await sequence.RunAsync(group, expected, target, null, cancellationToken).ConfigureAwait(false));
    }

    private static int DerivedNPop(string state, string direction) => state is "S3" or "S4" or "S5" || direction == "rev" ? 1 : 0;

    private static int ParseNPop(IReadOnlyDictionary<string, string> record, string field) =>
        record.TryGetValue(field, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
