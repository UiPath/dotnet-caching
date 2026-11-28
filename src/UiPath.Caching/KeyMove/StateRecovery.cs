using System.Globalization;

namespace UiPath.Caching.KeyMove;

internal sealed class StateRecovery(StateStore store, IStateReader samples, IRecordReader records, ITransitionSequence sequence, TimeProvider time, InstanceClaimReader? claims = null, InstanceClaimRecovery? claimRecovery = null)
{
    private static readonly string[] AlwaysCleared = [StateFields.Fg, StateFields.Rg, StateFields.PrevFg, StateFields.PrevRg, StateFields.AllAck, StateFields.T3At, StateFields.Swept, StateFields.CopiedAt, StateFields.DoneAt, StateFields.DryRun, StateFields.HoldAuto];

    /// <param name="registeredCaches">The caches the nodes registered (<c>ca</c>), used only for off; preparing to done take the set from the surviving records.</param>
    public async Task<RecoveryResult> RecreateAsync(string group, IReadOnlyList<PeerIdentity> peers, IReadOnlyList<string> registeredCaches, ClaimFields? claim, CancellationToken cancellationToken, SequenceOptions? sequenceOptions = null)
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

        var sample = await samples.SampleAsync(group, cancellationToken).ConfigureAwait(false);
        if (sample is not null && (sample.Conflicting || sample.Ep != identity.Ep || sample.Id != identity.Id || sample.State != identity.State || sample.Direction != identity.Direction))
        {
            return new RecoveryResult(RecoveryOutcome.RecordsDisagree);
        }

        var (caches, recorded, failure) = await ReadRecreationCachesAsync(group, identity, sample, registeredCaches, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        claim ??= await ClaimAsync(group, cancellationToken).ConfigureAwait(false);
        var target = RecreatedTarget(identity, sample, caches, recorded, claim);
        var result = await sequence.RunAsync(group, StateExpectation.Absent, target, sequenceOptions, cancellationToken).ConfigureAwait(false);
        await RepairMirrorAsync(group, result, cancellationToken).ConfigureAwait(false);
        return new RecoveryResult(RecoveryOutcome.Done, result);
    }

    /// <summary>Rebuilds the state from the control records of every slot. With no record, the operator names the state and the managed set. The new state keeps no residue counts, so the source fallback stays on, and carries <c>hold:op</c>.</summary>
    public async Task<RecoveryResult> ReassertAsync(string group, string confirm, (string State, string Direction)? named, IReadOnlyList<string>? caches, ClaimFields? claim, CancellationToken cancellationToken, SequenceOptions? sequenceOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirm);

        // Read the CAS expectation first so a concurrent state change rejects stale evidence.
        var stored = await store.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        var found = (await records.ReadAllAsync(group, cancellationToken).ConfigureAwait(false)).Where(r => r.Fields.ContainsKey(StateFields.Ep)).Select(r => r.Fields).ToArray();
        var again = await store.ReadAsync(group, cancellationToken).ConfigureAwait(false);
        if (again?.Identity != stored?.Identity || again?.Durable != stored?.Durable)
        {
            return new RecoveryResult(RecoveryOutcome.StateChanged, Detail: "the state changed while the records were read");
        }

        var (evidence, failure) = await SelectReassertEvidenceAsync(group, named, caches, stored, found).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        claim ??= await ClaimAsync(group, cancellationToken).ConfigureAwait(false);
        var target = ReassertedTarget(evidence, stored, claim);
        var expected = ExpectationOf(stored);
        var result = await sequence.RunAsync(group, expected, target, sequenceOptions, cancellationToken).ConfigureAwait(false);
        await RepairMirrorAsync(group, result, cancellationToken).ConfigureAwait(false);
        return new RecoveryResult(RecoveryOutcome.Done, result);
    }

    private static RecoveryEvidence? ReadRecoveryEvidence(IReadOnlyDictionary<string, string>[] found)
    {
        string state;
        string direction;
        int nPop;
        string? recordedFg = null;
        string? recordedRg = null;
        long committedEp;
        string committedId;
        var highest = found.Max(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture));
        var committed = found.Where(r => r.GetValueOrDefault(StateFields.Durable) == "1").ToArray();
        if (committed.Length > 0)
        {
            committedEp = committed.Max(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture));
            var atEp = committed.Where(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == committedEp).ToArray();
            if (atEp.Select(r => (r[StateFields.State], r[StateFields.Direction])).Distinct().Count() > 1)
            {
                return null;
            }

            (state, direction, nPop, committedId) = (atEp[0][StateFields.State], atEp[0][StateFields.Direction], ParseNPop(atEp[0], "np"), atEp[0][StateFields.Id]);

            // The generations come from the committed state: an uncommitted reverse never hands its retiring generation to the rebuilt one.
            (recordedFg, recordedRg) = (atEp[0].GetValueOrDefault(StateFields.Fg), atEp[0].GetValueOrDefault(StateFields.Rg));
        }
        else
        {
            // An uncommitted transition supplies its previous state.
            var top = found.First(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == highest);
            (state, direction, nPop, committedId) = (top["pst"], top["pdir"], ParseNPop(top, "pnp"), top["pid"]);
            committedEp = long.Parse(top["pep"], CultureInfo.InvariantCulture);
        }

        var gates = found.First(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == highest).Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).Select(k => k[2..]);
        return new RecoveryEvidence(state, direction, nPop, StateRecord.EncodeCaches(gates), highest, committedEp, committedId, recordedFg, recordedRg);
    }

    private static StateExpectation ExpectationOf(StateRecord? stored)
    {
        if (stored is null)
        {
            return StateExpectation.Absent;
        }

        return StateExpectation.Holding(stored.Ep, stored.Id, stored.Durable ? 1 : 0);
    }

    private static int DerivedNPop(string state, string direction) => state is KeyMoveStates.Copying or KeyMoveStates.Copied or KeyMoveStates.Done || direction == "rev" ? 1 : 0;

    // The generation comes from the committed record; with none shown, a forward run gets a new one, so the markers of the old one are obsolete.
    // The previous-generation fields are written with it, so the abandon of a recovery that stopped before its commit restores them.
    private static List<(string Field, string Value)> GenerationsAfterLoss(string state, string direction, string? recordedFg, string? recordedRg, string newId)
    {
        var set = new List<(string, string)>();
        if (state != KeyMoveStates.Off && direction == "fwd")
        {
            var fg = string.IsNullOrEmpty(recordedFg) ? newId : recordedFg;
            set.Add((StateFields.Fg, fg));
            set.Add((StateFields.PrevFg, fg));
        }
        else if (state == KeyMoveStates.Preparing && direction == "rev" && !string.IsNullOrEmpty(recordedRg))
        {
            set.Add((StateFields.Rg, recordedRg));
            set.Add((StateFields.PrevRg, recordedRg));
        }

        return set;
    }

    private static int ParseNPop(IReadOnlyDictionary<string, string> record, string field) =>
        record.TryGetValue(field, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private async Task<(RecoveryEvidence Evidence, RecoveryResult? Failure)> SelectReassertEvidenceAsync(
        string group, (string State, string Direction)? named, IReadOnlyList<string>? caches, StateRecord? stored, IReadOnlyDictionary<string, string>[] found)
    {
        if (found.Length > 0)
        {
            if (ReadRecoveryEvidence(found) is not { } evidence)
            {
                return (default, new RecoveryResult(RecoveryOutcome.RecordsDisagreeOnDirection));
            }

            if (stored is not null && evidence.Highest < stored.Ep)
            {
                return (default, new RecoveryResult(RecoveryOutcome.StateChanged, Detail: "the records are older than the state"));
            }

            return (evidence, null);
        }

        if (named is null || caches is null)
        {
            return (default, new RecoveryResult(RecoveryOutcome.NoRecords));
        }

        var (state, direction) = named.Value;
        var nPop = DerivedNPop(state, direction);
        var encoded = StateRecord.EncodeCaches(caches);
        var highest = Math.Max(stored?.Ep ?? 0, await store.ServerMicrosecondsAsync(group).ConfigureAwait(false));
        return (new RecoveryEvidence(state, direction, nPop, encoded, highest, highest, "0"), null);
    }

    private StateTarget ReassertedTarget(in RecoveryEvidence evidence, StateRecord? stored, ClaimFields? claim)
    {
        var newEp = Math.Max(evidence.Highest, stored?.Ep ?? 0) + 1;
        var clear = stored?.FieldNames?
            .Where(f => f.StartsWith(StateFields.ResiduePrefix, StringComparison.Ordinal) || f.StartsWith(StateFields.ApprovalPrefix, StringComparison.Ordinal) || AlwaysCleared.Contains(f))
            .ToArray() ?? [];
        var newId = MoveScripts.NewNonce();
        return new StateTarget(
            evidence.State,
            evidence.Direction,
            evidence.NPop,
            newEp,
            newId,
            StateRecord.FormatPrev(evidence.State, evidence.Direction, evidence.NPop, evidence.CommittedEp, evidence.CommittedId),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: newEp,
            Claim: claim?.Token,
            Caches: (evidence.Encoded, StateRecord.HashOfCaches(evidence.Encoded)),
            Move: claim?.Move,
            Release: claim?.Release,
            HoldOp: "reassert",
            Clear: clear,
            Set: GenerationsAfterLoss(evidence.State, evidence.Direction, evidence.Fg, evidence.Rg, newId));
    }

    private async Task<(string Caches, (string? Fg, string? Rg) Recorded, RecoveryResult? Failure)> ReadRecreationCachesAsync(
        string group, PeerIdentity identity, ControlSample? sample, IReadOnlyList<string> registeredCaches, CancellationToken cancellationToken)
    {
        if (identity.State == KeyMoveStates.Off)
        {
            return (StateRecord.EncodeCaches(registeredCaches), default, null);
        }

        if (sample is null)
        {
            return (string.Empty, default, new RecoveryResult(RecoveryOutcome.NeedsReassert));
        }

        // The identity and gates must come from one record read to reject a replacement or empty set.
        var fields = await records.ReadSampledAsync(group, sample, cancellationToken).ConfigureAwait(false);
        var gates = fields.Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).Select(k => k[2..]).ToArray();
        if (fields.GetValueOrDefault(StateFields.Ep) != identity.Ep.ToString(CultureInfo.InvariantCulture) || fields.GetValueOrDefault(StateFields.Id) != identity.Id || gates.Length == 0)
        {
            return (string.Empty, default, new RecoveryResult(RecoveryOutcome.NeedsReassert, Detail: "the record that named the managed set is gone, changed or holds no gate"));
        }

        // Only a committed record hands its generations on.
        var committed = fields.GetValueOrDefault(StateFields.Durable) == "1";
        return (StateRecord.EncodeCaches(gates), committed ? (fields.GetValueOrDefault(StateFields.Fg), fields.GetValueOrDefault(StateFields.Rg)) : default, null);
    }

    private StateTarget RecreatedTarget(PeerIdentity identity, ControlSample? sample, string caches, (string? Fg, string? Rg) recorded, ClaimFields? claim)
    {
        var newEp = Math.Max(identity.Ep, sample?.Ep ?? 0) + 1;
        var newId = MoveScripts.NewNonce();
        return new StateTarget(
            identity.State,
            identity.Direction,
            DerivedNPop(identity.State, identity.Direction),
            newEp,
            newId,
            StateRecord.FormatPrev(identity.State, identity.Direction, DerivedNPop(identity.State, identity.Direction), identity.Ep, identity.Id),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: newEp,
            Claim: claim?.Token,
            Caches: (caches, StateRecord.HashOfCaches(caches)),
            Move: claim?.Move,
            Release: claim?.Release,
            HoldOp: "recreated",
            Set: GenerationsAfterLoss(identity.State, identity.Direction, recorded.Fg, recorded.Rg, newId));
    }

    private Task<ClaimFields?> ClaimAsync(string group, CancellationToken cancellationToken) =>
        claims is null ? Task.FromResult<ClaimFields?>(null) : claims.FieldsAsync(group, cancellationToken);

    private async Task RepairMirrorAsync(string group, TransitionResult result, CancellationToken cancellationToken)
    {
        if (claimRecovery is not null && result.Outcome == TransitionOutcome.Completed)
        {
            await claimRecovery.RefreshAsync(group, await store.ReadAsync(group, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct RecoveryEvidence(string State, string Direction, int NPop, string Encoded, long Highest, long CommittedEp, string CommittedId, string? Fg = null, string? Rg = null);
}
