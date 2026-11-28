using System.Collections.Concurrent;
using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

/// <summary>A transition (TRANSIT, EPSYNC, barrier 1 with slot-map verification, COMMIT, barrier 2, DURABLE): each step is idempotent, so a run that stopped anywhere is resumed from the stored state.</summary>
/// <remarks>Barriers use the dedicated writer connection. A possibly applied transaction is retried only by a later resume.</remarks>
/// <param name="report">Told of <c>no_replica</c>, <c>barrier_failed</c>, <c>barrier_stuck</c> and <c>wait_skipped</c> with a detail text.</param>
/// <param name="time">The clock of the barriers; the system clock by default.</param>
internal sealed class TransitionSequence(IStateTopology topology, MoveKeyNames names, KeyMoveStateOptions options, Action<string, string>? report = null, TimeProvider? time = null) : ITransitionSequence
{
    private const int SlotBatch = 256;
    private const CommandFlags Master = CommandFlags.DemandMaster;

    public async Task<TransitionResult> RunAsync(string group, StateExpectation expected, StateTarget target, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        await using var episode = new Episode(topology, sequence ?? new SequenceOptions());
        if (LeaseLost(episode) is { } lost)
        {
            return lost;
        }

        var key = names.State(group);
        var (confirmed, written) = await StateStepAsync(episode, key, channel => channel.State.TransitWrite(key, expected, target, episode.Sequence.Fence), code => code is MoveScripts.Written or MoveScripts.Repeated, cancellationToken).ConfigureAwait(false);
        if (confirmed == BarrierResult.Refused && written == MoveScripts.LeaseNotHeld)
        {
            return new TransitionResult(TransitionOutcome.LeaseLost, Detail: "the server found the lease held by another controller");
        }

        if (confirmed == BarrierResult.Refused)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one this transition replaces");
        }

        if (confirmed == BarrierResult.Uncertain)
        {
            // Neither skipping barriers nor the way back clears it: the attempt stops here and a resume decides.
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the TRANSIT write of the state may have applied and is not repeated");
        }

        if (confirmed == BarrierResult.Skipped)
        {
            NoteSkipped(episode, "the TRANSIT write of the state");
        }
        else if (confirmed == BarrierResult.Failed && !SkipOrStuck(episode, "the TRANSIT write of the state"))
        {
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the state write is not on a replica");
        }

        return await CompleteAsync(group, episode, target.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>X12 toward the last committed state (<c>prev</c>), legal only before the commit: refused when the state is durable, a node acknowledged it, or any record at its <c>ep</c> shows <c>dur=1</c>. Nothing was acted on, so the barriers are skipped at once.</summary>
    public async Task<TransitionResult> AbandonAsync(string group, bool advertAcknowledges, long updatedAt, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        await using var probe = new Episode(topology, new SequenceOptions());
        var state = await ReadStateAsync(group, probe, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Durable || advertAcknowledges)
        {
            return new TransitionResult(TransitionOutcome.AbandonRefused, Detail: "the transition is committed, acknowledged or absent");
        }

        if (await AnyCommittedAsync(group, probe, state.Ep, cancellationToken).ConfigureAwait(false))
        {
            return new TransitionResult(TransitionOutcome.AbandonRefused, Detail: "a control record already shows dur=1 at this ep");
        }

        var prev = state.Prev.Split(':');
        var (restoreSet, restoreClear) = Generations.Restored(state);
        var target = new StateTarget(prev[0], prev[1], int.Parse(prev[2], System.Globalization.CultureInfo.InvariantCulture), state.Ep + 1, MoveScripts.NewNonce(), state.Prev, updatedAt, Clear: restoreClear, Set: restoreSet);
        return await RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 0, abandon: true), target, (sequence ?? new SequenceOptions()) with { SkipAtOnce = true, WayBack = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The rest of the transition the stored state shows at <c>dur=0</c>; a state that is already durable has its DURABLE write and WAIT repeated.</summary>
    public async Task<TransitionResult> ResumeAsync(string group, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        await using var episode = new Episode(topology, sequence ?? new SequenceOptions());
        return await CompleteAsync(group, episode, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IssueResult> IssueAsync(IMasterChannel channel, MoveKeyNames names, string group, int[] slots, SlotPhase phase, PhaseFlags flags, ConcurrentDictionary<int, EndPoint> misrouted)
    {
        foreach (var chunk in slots.Chunk(SlotBatch))
        {
            var codes = await Task.WhenAll(chunk.Select(slot => IssueSlotAsync(channel, names.Control(group, slot), slot, phase, misrouted))).ConfigureAwait(false);
            foreach (var code in codes)
            {
                if (code == MoveScripts.IdentityConflict)
                {
                    flags.Conflict = true;
                }
                else if (!phase.Accepts(code))
                {
                    flags.Refused = true;
                }
            }

            if (flags.Conflict || flags.Refused)
            {
                return IssueResult.Refused;
            }
        }

        return IssueResult.Done;
    }

    private static TransitionResult? LeaseLost(Episode episode) =>
        episode.Sequence.HoldsLease is { } holds && !holds() ? new TransitionResult(TransitionOutcome.LeaseLost, Detail: "the controller no longer holds the lease") : null;

    private static int ConfiguredReplicas(ClusterLayout layout, EndPoint master) =>
        layout.Masters.FirstOrDefault(m => EndPointMatch.Same(m.Endpoint, master))?.ConfiguredReplicas ?? throw new RedisException($"The topology does not list the master {master}.");

    // The owner is read before and after the write: a connection that sent it to another master would have its WAIT ask the wrong one.
    private static async Task<long> IssueSlotAsync(IMasterChannel channel, RedisKey key, int slot, SlotPhase phase, ConcurrentDictionary<int, EndPoint> misrouted)
    {
        var before = await channel.ControlOwnerAsync(key).ConfigureAwait(false);
        var code = await phase.Write(channel, key).RunAsync().ConfigureAwait(false);
        var after = await channel.ControlOwnerAsync(key).ConfigureAwait(false);
        if (!EndPointMatch.Same(channel.Endpoint, before) || !EndPointMatch.Same(channel.Endpoint, after))
        {
            misrouted[slot] = after ?? before ?? channel.Endpoint;
        }

        return code;
    }

    private static (IReadOnlyList<int> Unverified, IReadOnlyList<int> Pending) RouteRetries(
        ClusterLayout next,
        IReadOnlyList<int> slots,
        IReadOnlyList<int> moved,
        IReadOnlyList<int> pendingVerification,
        ConcurrentDictionary<int, EndPoint> misrouted,
        Dictionary<int, EndPoint> owners,
        HashSet<EndPoint> failedMasters)
    {
        foreach (var (slot, owner) in misrouted)
        {
            owners[slot] = owner;
        }

        foreach (var slot in moved)
        {
            owners.Remove(slot);
        }

        // A migrating slot can take the write on a different master, outside the original WAIT.
        var unsettled = slots.Where(next.Unsettled.Contains).ToArray();
        IReadOnlyList<int> unverified = [.. pendingVerification.Union(unsettled)];
        IReadOnlyList<int> pending = [.. pendingVerification.Union(moved).Union(misrouted.Keys).Union(unsettled).Where(slot => !failedMasters.Contains(owners.GetValueOrDefault(slot) ?? next.OwnerOf(slot)))];
        return (unverified, pending);
    }

    private static bool StateWriteSettled(BarrierResult result, ClusterLayout layout, RedisKey key) =>
        result != BarrierResult.Misrouted && !(result == BarrierResult.Confirmed && layout.Unsettled.Contains(HashSlot.Of(key.ToString())));

    private static BarrierResult UnwaitedResult(IssueResult result, BarrierResult success) => result switch
    {
        IssueResult.Done => success,
        IssueResult.Refused => BarrierResult.Refused,
        _ => BarrierResult.Misrouted,
    };

    // One transaction per slot, since a transaction holds the keys of one slot; a slot this connection does not send to the master is left out, and the caller issues it again on its owner.
    private static async Task<BoundIssue> BindSlotsAsync(IMasterChannel channel, MoveKeyNames names, string group, int[] slots, SlotPhase phase, PhaseFlags flags, ConcurrentDictionary<int, EndPoint> misrouted)
    {
        var owners = await Task.WhenAll(slots.Select(slot => channel.ControlOwnerAsync(names.Control(group, slot)))).ConfigureAwait(false);
        var sent = new List<(int Slot, RedisKey Key, MoveScripts.Write Write, EndPoint? Before)>(slots.Length);
        for (var i = 0; i < slots.Length; i++)
        {
            var key = names.Control(group, slots[i]);
            if (EndPointMatch.Same(channel.Endpoint, owners[i]))
            {
                sent.Add((slots[i], key, phase.Write(channel, key), owners[i]));
            }
            else
            {
                misrouted[slots[i]] = owners[i] ?? channel.Endpoint;
            }
        }

        await Task.WhenAll(sent.Select(s => s.Write.PrepareAsync())).ConfigureAwait(false);
        return new BoundIssue(
            channel.ControlDatabase,
            sent.Count == 0 ? default : sent[0].Key,
            [.. sent.Select(s => new KeyedWrite(s.Key, s.Write.Queue))],
            () => VerifySlotWritesAsync(channel, phase, flags, misrouted, sent),
            [.. sent.Select(s => s.Write)]);
    }

    private static async Task<IssueResult> IssueStateWriteAsync(IMasterChannel channel, RedisKey key, Func<IMasterChannel, MoveScripts.Write> write, Func<long, bool> accepts, StateWriteObservation observed)
    {
        var before = await channel.StateOwnerAsync(key).ConfigureAwait(false);
        observed.Code = await write(channel).RunAsync().ConfigureAwait(false);
        if (!accepts(observed.Code))
        {
            return IssueResult.Refused;
        }

        var after = await channel.StateOwnerAsync(key).ConfigureAwait(false);
        if (EndPointMatch.Same(channel.Endpoint, before) && EndPointMatch.Same(channel.Endpoint, after))
        {
            return IssueResult.Done;
        }

        observed.Actual = after ?? before;
        return IssueResult.Misrouted;
    }

    private static async Task<BoundIssue> BindStateWriteAsync(IMasterChannel channel, RedisKey key, Func<IMasterChannel, MoveScripts.Write> write, Func<long, bool> accepts, StateWriteObservation observed)
    {
        var scripted = write(channel);
        var before = await channel.StateOwnerAsync(key).ConfigureAwait(false);
        if (!EndPointMatch.Same(channel.Endpoint, before))
        {
            // This connection would send the write to another master: it is not sent, and the next hop issues it on the owner.
            observed.Actual = before;
            return new BoundIssue(channel.StateDatabase, key, [], () => Task.FromResult<IssueResult?>(IssueResult.Misrouted), [scripted]);
        }

        await scripted.PrepareAsync().ConfigureAwait(false);
        return new BoundIssue(
            channel.StateDatabase,
            key,
            [new KeyedWrite(key, scripted.Queue)],
            async () =>
            {
                if (scripted.Code is not { } written)
                {
                    return null;
                }

                observed.Code = written;
                if (!accepts(written))
                {
                    return IssueResult.Refused;
                }

                var after = await channel.StateOwnerAsync(key).ConfigureAwait(false);
                if (EndPointMatch.Same(channel.Endpoint, after))
                {
                    return IssueResult.Done;
                }

                observed.Actual = after;
                return IssueResult.Misrouted;
            },
            [scripted]);
    }

    private static async Task<BarrierResult> ConfirmUnwaitedAsync(Episode episode, EndPoint master, Func<IMasterChannel, Task<IssueResult>> issue, BarrierResult success, CancellationToken cancellationToken)
    {
        var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
        return episode.TryDispatch()
            ? UnwaitedResult(await issue(channel).ConfigureAwait(false), success)
            : BarrierResult.Failed;
    }

    private static async Task<IssueResult?> VerifySlotWritesAsync(
        IMasterChannel channel,
        SlotPhase phase,
        PhaseFlags flags,
        ConcurrentDictionary<int, EndPoint> misrouted,
        List<(int Slot, RedisKey Key, MoveScripts.Write Write, EndPoint? Before)> sent)
    {
        if (sent.Any(s => s.Write.Code is null))
        {
            return null;
        }

        foreach (var (_, _, write, _) in sent)
        {
            if (write.Code == MoveScripts.IdentityConflict)
            {
                flags.Conflict = true;
            }
            else if (write.Code is { } code && !phase.Accepts(code))
            {
                flags.Refused = true;
            }
        }

        if (flags.Conflict || flags.Refused)
        {
            return IssueResult.Refused;
        }

        foreach (var (slot, key, _, before) in sent)
        {
            var after = await channel.ControlOwnerAsync(key).ConfigureAwait(false);
            if (!EndPointMatch.Same(channel.Endpoint, after))
            {
                misrouted[slot] = after ?? before ?? channel.Endpoint;
            }
        }

        return IssueResult.Done;
    }

    private async Task<TransitionResult> CompleteAsync(string group, Episode episode, string? expectedId, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(group, episode, cancellationToken).ConfigureAwait(false);
        if (state is null || (expectedId is not null && state.Id != expectedId))
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one being completed");
        }

        // A durable state may be one whose DURABLE reply or WAIT was lost: the write and its WAIT are repeated, never trusted.
        if (state.Durable)
        {
            return LeaseLost(episode) ?? await DurableStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
        }

        if (await FinishCommitAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            return failure;
        }

        if (episode.Sequence.BeforeDurable is { } beforeDurable && await beforeDurable(cancellationToken).ConfigureAwait(false) is { } halted)
        {
            return halted;
        }

        return LeaseLost(episode) ?? await DurableStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransitionResult?> FinishCommitAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var committed = false;
        for (var attempt = 0; attempt < options.BarrierRetries && !committed; attempt++)
        {
            var (result, retry) = await RunCommitAttemptAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
            if (retry)
            {
                continue;
            }

            if (result is not null)
            {
                return result;
            }

            committed = true;
        }

        if (!committed)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete, Detail: "a control record would not take the commit marker");
        }

        return null;
    }

    private async Task<(TransitionResult? Result, bool Retry)> RunCommitAttemptAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        if (LeaseLost(episode) is { } beforeSync)
        {
            return (beforeSync, false);
        }

        if (await SyncStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } syncFailure)
        {
            return (syncFailure, false);
        }

        if (LeaseLost(episode) is { } beforeCommit)
        {
            return (beforeCommit, false);
        }

        if (episode.Sequence.BeforeCommit is { } hook && await hook(cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return (stopped, false);
        }

        if (await CommitBeginStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } begun)
        {
            return (begun, false);
        }

        var result = await CommitStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
        return (result, result?.Outcome == TransitionOutcome.CommitIncomplete);
    }

    // Marks on the state, and confirms on a replica, that the commit of this identity begins, so an abandon of it is refused from here on and a failover cannot lose the marker.
    private async Task<TransitionResult?> CommitBeginStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var (confirmed, code) = await StateStepAsync(episode, key, channel => channel.State.CommitBeginWrite(key, state.Id, episode.Sequence.Fence), c => c == MoveScripts.Written, cancellationToken).ConfigureAwait(false);
        switch (confirmed)
        {
            case BarrierResult.Refused when code == MoveScripts.LeaseNotHeld:
                return new TransitionResult(TransitionOutcome.LeaseLost, Detail: "the server found the lease held by another controller");
            case BarrierResult.Refused:
                return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the identity was replaced before its commit began");
            case BarrierResult.Uncertain:
                return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the commit marker may have applied and is not repeated");
            case BarrierResult.Skipped:
                NoteSkipped(episode, "the commit marker");
                return null;
            case BarrierResult.Failed when !SkipOrStuck(episode, "the commit marker"):
                return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the commit marker is not on a replica");
            default:
                return null;
        }
    }

    private async Task<TransitionResult?> SyncStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var record = state.ToControlRecord([.. state.CacheIds.Select(cid => (cid, MoveScripts.NewNonce()))]);

        // The default limit is written on the records' field count: the fixed fields, the cache list, one gate per cache and the markers a record may hold. The leader's LIMSET changes it later.
        if (state is { State: KeyMoveStates.Copying, Direction: "fwd" })
        {
            record = record with { Limit = MarkerSweeper.Floor(state) + options.MarkerLimit };
        }

        var phase = new SlotPhase(state.Ep, state.Id, NeedDurable: false, (channel, key) => channel.Control.ControlSyncWrite(key, record), code => code is MoveScripts.Written or MoveScripts.AlreadyWrittenOrOlder);
        var result = await RunSlotPhaseAsync(group, episode, phase, cancellationToken).ConfigureAwait(false);
        if (result.Conflict)
        {
            return new TransitionResult(TransitionOutcome.IdentityConflict, Detail: "a control record holds another identity at this ep, or a higher ep");
        }

        if (result.Terminal)
        {
            report?.Invoke("barrier_stuck", "an epoch sync write may have applied in part");
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "an epoch sync write may have applied in part and is not repeated");
        }

        if (result.Unverified > 0)
        {
            report?.Invoke("barrier_stuck", $"{result.Unverified} control records do not hold the target identity");
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "a control record does not hold the target identity");
        }

        if (result.BarrierSkipped)
        {
            NoteSkipped(episode, "barrier 1");
        }

        if (result.BarrierFailed && !SkipOrStuck(episode, "barrier 1"))
        {
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "a master did not confirm the control records");
        }

        return null;
    }

    private async Task<TransitionResult?> CommitStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var phase = new SlotPhase(state.Ep, state.Id, NeedDurable: true, (channel, key) => channel.Control.ControlCommitWrite(key, state.Ep, state.Id), code => code == MoveScripts.Written);
        var result = await RunSlotPhaseAsync(group, episode, phase, cancellationToken).ConfigureAwait(false);
        if (result.Conflict)
        {
            return new TransitionResult(TransitionOutcome.IdentityConflict, Detail: "a control record holds another identity at this ep, or a higher ep");
        }

        if (result.Terminal)
        {
            // A batch that may have applied in part is never sent again by this call, whatever else the phase saw: resume decides.
            return new TransitionResult(TransitionOutcome.CommitUncertain, Detail: "a commit write may have applied in part and a record is not committed");
        }

        if (result.Refused || result.Unverified > 0)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete);
        }

        if (result.BarrierFailed || result.BarrierSkipped)
        {
            NoteSkipped(episode, "barrier 2");
        }

        return null;
    }

    private async Task<TransitionResult> DurableStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var (confirmed, _) = await StateStepAsync(episode, key, channel => channel.State.DurableWrite(key, state.Id, episode.Sequence.Release), code => code == MoveScripts.Written, cancellationToken).ConfigureAwait(false);
        if (confirmed == BarrierResult.Refused)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored id changed before DURABLE");
        }

        if (confirmed == BarrierResult.Uncertain)
        {
            // Never Completed: the write may not have landed, and what the driver does after a completion must not run. A resume repeats DURABLE and its WAIT.
            var landed = await DurableLandedAsync(group, state.Id, episode, cancellationToken).ConfigureAwait(false);
            return new TransitionResult(TransitionOutcome.CommitUncertain, Detail: landed ? "DURABLE landed but its replication is unconfirmed" : "DURABLE may not have landed");
        }

        if (confirmed is BarrierResult.Failed or BarrierResult.Skipped)
        {
            NoteSkipped(episode, "the DURABLE write of the state");
        }

        return new TransitionResult(TransitionOutcome.Completed, episode.WaitSkipped);
    }

    private async Task<bool> DurableLandedAsync(string group, string id, Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadStateAsync(group, episode, cancellationToken).ConfigureAwait(false) is { Durable: true } read && read.Id == id;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return false;
        }
    }

    // Issues the phase on every slot, waits per master, then reads the topology and every record again: a slot that moved or does not hold the target is issued once more on its owner.
    private async Task<SlotPhaseResult> RunSlotPhaseAsync(string group, Episode episode, SlotPhase phase, CancellationToken cancellationToken)
    {
        var slots = names.SlotSet();
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<int> pending = slots;
        IReadOnlyList<int> unverified = slots;
        var failedMasters = new HashSet<EndPoint>();
        var owners = new Dictionary<int, EndPoint>();
        var skipped = false;
        var flags = new PhaseFlags();
        for (var round = 0; round < options.BarrierRetries && pending.Count > 0; round++)
        {
            var misrouted = new ConcurrentDictionary<int, EndPoint>();
            var confirmations = await ConfirmSlotOwnersAsync(group, episode, phase, layout, pending, owners, flags, misrouted, cancellationToken).ConfigureAwait(false);
            failedMasters.UnionWith(confirmations.Where(c => c.Result is BarrierResult.Failed or BarrierResult.Uncertain).Select(c => c.Key));
            if (confirmations.Any(c => c.Result == BarrierResult.Uncertain))
            {
                // No slot, whoever owns it now, is issued again once a write of the phase may have applied.
                return new SlotPhaseResult(flags.Conflict, flags.Refused, slots.Count, true, skipped, true);
            }

            skipped |= confirmations.Any(c => c.Result == BarrierResult.Skipped);
            if (flags.Conflict || flags.Refused)
            {
                return new SlotPhaseResult(flags.Conflict, flags.Refused, 0, false, false, false);
            }

            var next = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            var moved = next.MovedSince(layout, slots);
            var (pendingVerification, conflict) = await VerifyAsync(group, episode, next, slots, phase, cancellationToken).ConfigureAwait(false);
            if (conflict)
            {
                return new SlotPhaseResult(true, false, 0, false, false, false);
            }

            layout = next;
            (unverified, pending) = RouteRetries(next, slots, moved, pendingVerification, misrouted, owners, failedMasters);
        }

        return new SlotPhaseResult(false, false, unverified.Union(pending).Count(), failedMasters.Count > 0, skipped, false);
    }

    private Task<(EndPoint Key, BarrierResult Result)[]> ConfirmSlotOwnersAsync(
        string group,
        Episode episode,
        SlotPhase phase,
        ClusterLayout current,
        IReadOnlyList<int> pending,
        Dictionary<int, EndPoint> owners,
        PhaseFlags flags,
        ConcurrentDictionary<int, EndPoint> misrouted,
        CancellationToken cancellationToken) =>
        Task.WhenAll(pending.GroupBy(slot => owners.GetValueOrDefault(slot) ?? current.OwnerOf(slot)).Select(async owned =>
        {
            // An ASK redirect cannot be confirmed on the original master.
            var ownedSlots = owned.Where(slot => !current.Unsettled.Contains(slot)).ToArray();
            foreach (var left in owned.Where(slot => current.Unsettled.Contains(slot)))
            {
                misrouted[left] = owned.Key;
            }

            var result = await ConfirmAsync(
                episode,
                owned.Key,
                ConfiguredReplicas(current, owned.Key),
                channel => IssueAsync(channel, names, group, ownedSlots, phase, flags, misrouted),
                channel => BindSlotsAsync(channel, names, group, ownedSlots, phase, flags, misrouted),
                cancellationToken).ConfigureAwait(false);
            return (owned.Key, result);
        }));

    private async Task<bool> AnyCommittedAsync(string group, Episode episode, long ep, CancellationToken cancellationToken)
    {
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        var slots = names.SlotSet();
        var databases = new Dictionary<EndPoint, IDatabase>();
        foreach (var owner in slots.Select(layout.OwnerOf).Distinct())
        {
            databases[owner] = (await episode.Channels.GetAsync(owner, cancellationToken).ConfigureAwait(false)).ControlDatabase;
        }

        var reads = slots.Select(slot => databases[layout.OwnerOf(slot)].HashGetAsync(names.Control(group, slot), [StateFields.Ep, StateFields.Durable], Master)).ToArray();
        await Task.WhenAll(reads).ConfigureAwait(false);
        return reads.Any(r => r.Result[1] == "1" && r.Result[0] == ep);
    }

    private async Task<(IReadOnlyList<int> Pending, bool Conflict)> VerifyAsync(string group, Episode episode, ClusterLayout layout, IReadOnlyList<int> slots, SlotPhase phase, CancellationToken cancellationToken)
    {
        var databases = new Dictionary<EndPoint, IDatabase>();
        foreach (var owner in slots.Select(layout.OwnerOf).Distinct())
        {
            databases[owner] = (await episode.Channels.GetAsync(owner, cancellationToken).ConfigureAwait(false)).ControlDatabase;
        }

        var reads = slots
            .Select(slot => (slot, read: databases[layout.OwnerOf(slot)].HashGetAsync(names.Control(group, slot), [StateFields.Ep, StateFields.Id, StateFields.Durable], Master)))
            .ToArray();
        await Task.WhenAll(reads.Select(r => r.read)).ConfigureAwait(false);
        var pending = new List<int>();
        foreach (var (slot, read) in reads)
        {
            var held = read.Result;
            if (held[0].IsNull || !long.TryParse((string?)held[0], out var ep) || ep < phase.TargetEp)
            {
                pending.Add(slot);
            }
            else if (ep > phase.TargetEp || held[1] != phase.TargetId)
            {
                return (pending, true);
            }
            else if (phase.NeedDurable && held[2] != "1")
            {
                pending.Add(slot);
            }
        }

        return (pending, false);
    }

    // The writes of one master, then WAIT proven to run on the connection that wrote; a barrier that did not confirm is tried again on a replaced connection, unless its transaction may have applied.
    private async Task<BarrierResult> ConfirmAsync(
        Episode episode,
        EndPoint master,
        int configuredReplicas,
        Func<IMasterChannel, Task<IssueResult>> issue,
        Func<IMasterChannel, Task<BoundIssue>> bind,
        CancellationToken cancellationToken)
    {
        if (configuredReplicas < 0)
        {
            throw new InvalidOperationException("The number of replicas of the master is not configured, so a replication barrier cannot tell a missing replica from none; set ExpectedReplicasPerMaster (0 for none).");
        }

        var required = Math.Min(options.StateWaitReplicas, configuredReplicas);
        if (required <= 0)
        {
            if (configuredReplicas == 0 && episode.NoReplica.TryAdd(master, 0))
            {
                report?.Invoke("no_replica", master.ToString()!);
            }

            return await ConfirmUnwaitedAsync(episode, master, issue, BarrierResult.Confirmed, cancellationToken).ConfigureAwait(false);
        }

        var seq = episode.Sequence;
        if (seq.SkipAtOnce || (seq.WayBack && seq.Ledger?.FailedRecently(master) == true))
        {
            return await ConfirmUnwaitedAsync(episode, master, issue, BarrierResult.Skipped, cancellationToken).ConfigureAwait(false);
        }

        return await ConfirmWithReplicasAsync(episode, master, bind, seq, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BarrierResult> ConfirmWithReplicasAsync(Episode episode, EndPoint master, Func<IMasterChannel, Task<BoundIssue>> bind, SequenceOptions seq, CancellationToken cancellationToken)
    {
        var terminal = false;
        for (var attempt = 0; attempt < options.BarrierRetries; attempt++)
        {
            if (episode.Terminal)
            {
                // Another master of this attempt is uncertain: no further write is sent. What this one already sent was settled by its barrier.
                return BarrierResult.Failed;
            }

            var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
            try
            {
                var (result, sameConnection) = await RunReplicatedAttemptAsync(episode, channel, bind, cancellationToken).ConfigureAwait(false);
                if (result == BarrierResult.Uncertain)
                {
                    terminal = true;
                    break;
                }

                if (result is { } completed)
                {
                    return completed;
                }

                if (sameConnection)
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Retry the idempotent writes on a replacement connection.
            }

            if (attempt + 1 < options.BarrierRetries)
            {
                await episode.Channels.ReplaceAsync(master, cancellationToken).ConfigureAwait(false);
            }
        }

        seq.Ledger?.Record(master);
        report?.Invoke("barrier_failed", master.ToString()!);
        return terminal ? BarrierResult.Uncertain : BarrierResult.Failed;
    }

    private async Task<(BarrierResult? Result, bool SameConnection)> RunReplicatedAttemptAsync(Episode episode, IMasterChannel channel, Func<IMasterChannel, Task<BoundIssue>> bind, CancellationToken cancellationToken)
    {
        var bound = await bind(channel).ConfigureAwait(false);
        if (bound.Writes.Count == 0)
        {
            return (BarrierResult.Misrouted, false);
        }

        var barrier = new ServerBarrier(bound.Database, bound.Owner, time ?? TimeProvider.System, replicaFree: false)
        {
            Retries = 0,
            Timeout = options.BarrierWait,
        };
        if (!episode.TryDispatch())
        {
            // The check and the dispatch are one step: a write registered here before another master turned uncertain is settled by its barrier, one after it is never sent.
            return (BarrierResult.Failed, false);
        }

        var outcome = await barrier.WriteManyByKeyAndWaitAsync(bound.Writes, wayBack: false).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (barrier.LastError is not null)
        {
            // An EXEC whose reply was lost, or a transaction that was partly applied: never sent again, and no routing or result check may say otherwise.
            episode.MarkTerminal();
            return (BarrierResult.Uncertain, false);
        }

        var issued = await bound.ResultAsync().ConfigureAwait(false);
        if (issued is IssueResult.Refused or IssueResult.Misrouted)
        {
            return (UnwaitedResult(issued.Value, BarrierResult.Confirmed), false);
        }

        if (issued == IssueResult.Done && outcome is BarrierOutcome.Confirmed or BarrierOutcome.NoReplica)
        {
            return (BarrierResult.Confirmed, false);
        }

        // A script the server discards at queue time looks like any other abort; one that is refused for good is replaced by the fallback on the next try, on the same connection that learned it.
        if (issued is null && bound.Scripted.FirstOrDefault(static w => w.Code is null) is { } unrun && await unrun.ScriptsRefusedAsync().ConfigureAwait(false))
        {
            return (null, true);
        }

        return (null, false);
    }

    // A state-key write, the check of which master got it, and the WAIT on that master's connection; a write sent elsewhere is issued again on the owner.
    private async Task<(BarrierResult Result, long Code)> StateStepAsync(Episode episode, RedisKey key, Func<IMasterChannel, MoveScripts.Write> write, Func<long, bool> accepts, CancellationToken cancellationToken)
    {
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        var owner = layout.OwnerOf(HashSlot.Of(key.ToString()));
        var observed = new StateWriteObservation();
        for (var hop = 0; hop < options.BarrierRetries; hop++)
        {
            observed.Actual = null;
            var result = await ConfirmAsync(
                episode,
                owner,
                ConfiguredReplicas(layout, owner),
                channel => IssueStateWriteAsync(channel, key, write, accepts, observed),
                channel => BindStateWriteAsync(channel, key, write, accepts, observed),
                cancellationToken).ConfigureAwait(false);
            layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            if (StateWriteSettled(result, layout, key))
            {
                return (result, observed.Code);
            }

            owner = observed.Actual ?? layout.OwnerOf(HashSlot.Of(key.ToString()));
        }

        return (BarrierResult.Failed, observed.Code);
    }

    private void NoteSkipped(Episode episode, string what)
    {
        episode.WaitSkipped = true;
        report?.Invoke("wait_skipped", what);
    }

    private bool SkipOrStuck(Episode episode, string what)
    {
        if (episode.Sequence.SkipBarriers || episode.Sequence.WayBack)
        {
            episode.WaitSkipped = true;
            report?.Invoke("wait_skipped", what);
            return true;
        }

        report?.Invoke("barrier_stuck", what);
        return false;
    }

    private async Task<EndPoint> StateMasterAsync(RedisKey key, CancellationToken cancellationToken)
    {
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        return layout.OwnerOf(HashSlot.Of(key.ToString()));
    }

    private async Task<StateRecord?> ReadStateAsync(string group, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var master = await StateMasterAsync(key, cancellationToken).ConfigureAwait(false);
        var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
        return StateRecord.Parse(await channel.StateDatabase.HashGetAllAsync(key, Master).ConfigureAwait(false));
    }

    private sealed record BoundIssue(IDatabase Database, RedisKey Owner, IReadOnlyList<KeyedWrite> Writes, Func<Task<IssueResult?>> ResultAsync, IReadOnlyList<MoveScripts.Write> Scripted);

    private sealed class StateWriteObservation
    {
        public long Code { get; set; }

        public EndPoint? Actual { get; set; }
    }

    private sealed record SlotPhase(long TargetEp, string TargetId, bool NeedDurable, Func<IMasterChannel, RedisKey, MoveScripts.Write> Write, Func<long, bool> Accepts);

    private sealed record SlotPhaseResult(bool Conflict, bool Refused, int Unverified, bool BarrierFailed, bool BarrierSkipped, bool Terminal);

    private sealed class PhaseFlags
    {
        private int _conflict;
        private int _refused;

        public bool Conflict
        {
            get => Volatile.Read(ref _conflict) == 1;
            set => Volatile.Write(ref _conflict, value ? 1 : 0);
        }

        public bool Refused
        {
            get => Volatile.Read(ref _refused) == 1;
            set => Volatile.Write(ref _refused, value ? 1 : 0);
        }
    }

    private sealed class Episode(IStateTopology topology, SequenceOptions sequence) : IAsyncDisposable
    {
#if NET9_0_OR_GREATER
        private readonly Lock _gate = new();
#else
        private readonly object _gate = new();
#endif
        private bool _terminal;

        public MasterChannels Channels { get; } = new(topology);

        public SequenceOptions Sequence { get; } = sequence;

        public ConcurrentDictionary<EndPoint, byte> NoReplica { get; } = new();

        public bool WaitSkipped { get; set; }

        /// <summary>Set by the first barrier whose write is uncertain; every barrier of the episode stops before its next attempt.</summary>
        public bool Terminal
        {
            get
            {
                lock (_gate)
                {
                    return _terminal;
                }
            }
        }

        public void MarkTerminal()
        {
            lock (_gate)
            {
                _terminal = true;
            }
        }

        /// <summary>Whether any write of the episode, with a barrier or without one, may be sent: false once any barrier is uncertain. A true answer registers the dispatch, so uncertainty that follows lets the writes settle.</summary>
        public bool TryDispatch()
        {
            lock (_gate)
            {
                return !_terminal;
            }
        }

        public ValueTask DisposeAsync() => Channels.DisposeAsync();
    }
}
