using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed class StateRecovery(StateStore store, ControlSampler sampler, ControlRecordReader records, IDatabase controlDatabase, TransitionSequence sequence, MoveKeyNames names, TimeProvider time)
{
    private static readonly string[] AlwaysCleared = [StateFields.AllAck, StateFields.Swept, StateFields.DrainedAt, StateFields.ClosedAt, StateFields.DryRun, StateFields.HoldAuto];

    /// <param name="registeredCaches">The caches the nodes registered (<c>ca</c>), used only for S0; S1 to S5 take the set from the surviving records.</param>
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

        var sample = await sampler.SampleAsync(group, cancellationToken).ConfigureAwait(false);
        if (sample is not null && (sample.Conflicting || sample.Ep != identity.Ep || sample.Id != identity.Id || sample.State != identity.State || sample.Direction != identity.Direction))
        {
            return new RecoveryResult(RecoveryOutcome.RecordsDisagree);
        }

        var (caches, failure) = await ReadRecreationCachesAsync(group, identity, sample, registeredCaches).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var target = RecreatedTarget(identity, sample, caches!, claim);
        var result = await sequence.RunAsync(group, StateExpectation.Absent, target, sequenceOptions, cancellationToken).ConfigureAwait(false);
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

        var target = ReassertedTarget(evidence, stored, claim);
        var expected = ExpectationOf(stored);
        return new RecoveryResult(RecoveryOutcome.Done, await sequence.RunAsync(group, expected, target, sequenceOptions, cancellationToken).ConfigureAwait(false));
    }

    private static RecoveryEvidence? ReadRecoveryEvidence(IReadOnlyDictionary<string, string>[] found)
    {
        string state;
        string direction;
        int nPop;
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
        }
        else
        {
            // An uncommitted transition supplies its previous state.
            var top = found.First(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == highest);
            (state, direction, nPop, committedId) = (top["pst"], top["pdir"], ParseNPop(top, "pnp"), top["pid"]);
            committedEp = long.Parse(top["pep"], CultureInfo.InvariantCulture);
        }

        var gates = found.First(r => long.Parse(r[StateFields.Ep], CultureInfo.InvariantCulture) == highest).Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).Select(k => k[2..]);
        return new RecoveryEvidence(state, direction, nPop, StateRecord.EncodeCaches(gates), highest, committedEp, committedId);
    }

    private static StateExpectation ExpectationOf(StateRecord? stored)
    {
        if (stored is null)
        {
            return StateExpectation.Absent;
        }

        return StateExpectation.Holding(stored.Ep, stored.Id, stored.Durable ? 1 : 0);
    }

    private static int DerivedNPop(string state, string direction) => state is "S3" or "S4" or "S5" || direction == "rev" ? 1 : 0;

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
        return new StateTarget(
            evidence.State,
            evidence.Direction,
            evidence.NPop,
            newEp,
            MoveScripts.NewNonce(),
            StateRecord.FormatPrev(evidence.State, evidence.Direction, evidence.NPop, evidence.CommittedEp, evidence.CommittedId),
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            MoveId: newEp,
            Claim: claim?.Token,
            Caches: (evidence.Encoded, StateRecord.HashOfCaches(evidence.Encoded)),
            Move: claim?.Move,
            Release: claim?.Release,
            HoldOp: "reassert",
            Clear: clear);
    }

    private async Task<(string? Caches, RecoveryResult? Failure)> ReadRecreationCachesAsync(
        string group, PeerIdentity identity, ControlSample? sample, IReadOnlyList<string> registeredCaches)
    {
        if (identity.State == "S0")
        {
            return (StateRecord.EncodeCaches(registeredCaches), null);
        }

        if (sample is null)
        {
            return (null, new RecoveryResult(RecoveryOutcome.NeedsReassert));
        }

        // The identity and gates must come from one record read to reject a replacement or empty set.
        var record = await controlDatabase.HashGetAllAsync(names.Control(group, sample.Slot), CommandFlags.DemandMaster).ConfigureAwait(false);
        var fields = record.ToDictionary(e => e.Name.ToString(), e => (string?)e.Value);
        var gates = fields.Keys.Where(k => k.StartsWith("c:", StringComparison.Ordinal)).Select(k => k[2..]).ToArray();
        if (fields.GetValueOrDefault(StateFields.Ep) != identity.Ep.ToString(CultureInfo.InvariantCulture) || fields.GetValueOrDefault(StateFields.Id) != identity.Id || gates.Length == 0)
        {
            return (null, new RecoveryResult(RecoveryOutcome.NeedsReassert, Detail: "the record that named the managed set is gone, changed or holds no gate"));
        }

        return (StateRecord.EncodeCaches(gates), null);
    }

    private StateTarget RecreatedTarget(PeerIdentity identity, ControlSample? sample, string caches, ClaimFields? claim)
    {
        var newEp = Math.Max(identity.Ep, sample?.Ep ?? 0) + 1;
        return new StateTarget(
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
    }

    private readonly record struct RecoveryEvidence(string State, string Direction, int NPop, string Encoded, long Highest, long CommittedEp, string CommittedId);
}
