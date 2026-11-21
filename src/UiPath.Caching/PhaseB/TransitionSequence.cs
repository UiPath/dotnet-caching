using System.Collections.Concurrent;
using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// Steps 1 to 6 of a transition (TRANSIT, EPSYNC, barrier 1 with slot-map verification, COMMIT, barrier 2, DURABLE): each step is idempotent, so a run that stopped anywhere is resumed from the stored state.
/// </summary>
/// <param name="report">Told of <c>no_replica</c>, <c>barrier_failed</c>, <c>barrier_stuck</c> and <c>wait_skipped</c> with a detail text.</param>
internal sealed class TransitionSequence(IStateTopology topology, MoveKeyNames names, PhaseBStateOptions options, Action<string, string>? report = null)
{
    private const int SlotBatch = 256;
    private const CommandFlags Master = CommandFlags.DemandMaster;

    /// <summary>Steps 1 to 6 for <paramref name="target"/>, which replaces the state held under <paramref name="expected"/>.</summary>
    public async Task<TransitionResult> RunAsync(string group, StateExpectation expected, StateTarget target, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        await using var episode = new Episode(topology, sequence ?? new SequenceOptions());
        if (LeaseLost(episode) is { } lost)
        {
            return lost;
        }

        var key = names.State(group);
        var (confirmed, _) = await StateStepAsync(episode, key, channel => channel.State.TransitAsync(key, expected, target), code => code != MoveScripts.ExpectationFailed, cancellationToken).ConfigureAwait(false);
        if (confirmed == BarrierResult.Refused)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one this transition replaces");
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

    /// <summary>
    /// X12 toward the last committed state (<c>prev</c>), legal only before step 4: refused when the state is durable, a node acknowledged it, or any record at its <c>ep</c> shows <c>dur=1</c>.
    /// Nothing was acted on, so the barriers are skipped at once.
    /// </summary>
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
        var target = new StateTarget(prev[0], prev[1], int.Parse(prev[2], System.Globalization.CultureInfo.InvariantCulture), state.Ep + 1, MoveScripts.NewNonce(), state.Prev, updatedAt);
        return await RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 0, abandon: true), target, (sequence ?? new SequenceOptions()) with { SkipAtOnce = true, WayBack = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Steps 2 to 6 for the transition the stored state shows at <c>dur=0</c>; a state that is already durable has its DURABLE write and WAIT repeated.</summary>
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
        layout.Masters.FirstOrDefault(m => m.Endpoint.Equals(master))?.ConfiguredReplicas ?? throw new RedisException($"The topology does not list the master {master}.");

    // The owner is read before and after the write: a connection that sent it to another master would have its WAIT ask the wrong one.
    private static async Task<long> IssueSlotAsync(IMasterChannel channel, RedisKey key, int slot, SlotPhase phase, ConcurrentDictionary<int, EndPoint> misrouted)
    {
        var before = await channel.ControlOwnerAsync(key).ConfigureAwait(false);
        var code = await phase.Issue(channel, key).ConfigureAwait(false);
        var after = await channel.ControlOwnerAsync(key).ConfigureAwait(false);
        if (!channel.Endpoint.Equals(before) || !channel.Endpoint.Equals(after))
        {
            misrouted[slot] = after ?? before ?? channel.Endpoint;
        }

        return code;
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

        var committed = false;
        for (var attempt = 0; attempt < options.BarrierRetries && !committed; attempt++)
        {
            if (LeaseLost(episode) is { } beforeSync)
            {
                return beforeSync;
            }

            if (await SyncStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } syncFailure)
            {
                return syncFailure;
            }

            if (LeaseLost(episode) is { } beforeCommit)
            {
                return beforeCommit;
            }

            if (await CommitBeginStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } begun)
            {
                return begun;
            }

            var commit = await CommitStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
            if (commit is null)
            {
                committed = true;
            }
            else if (commit.Outcome != TransitionOutcome.CommitIncomplete)
            {
                return commit;
            }
        }

        if (!committed)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete, Detail: "a control record would not take the commit marker");
        }

        return LeaseLost(episode) ?? await DurableStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
    }

    // Marks on the state that the commit of this identity begins, so an abandon of it is refused from here on; refused itself when the identity was replaced.
    private async Task<TransitionResult?> CommitBeginStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var (master, _) = await StateMasterAsync(key, cancellationToken).ConfigureAwait(false);
        var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
        return await channel.State.CommitBeginAsync(key, state.Id).ConfigureAwait(false) == MoveScripts.Written
            ? null
            : new TransitionResult(TransitionOutcome.StateChanged, Detail: "the identity was replaced before its commit began");
    }

    // Steps 2 and 3. Null when every slot holds the target identity and its master confirmed the writes.
    private async Task<TransitionResult?> SyncStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var record = state.ToControlRecord([.. state.CacheIds.Select(cid => (cid, MoveScripts.NewNonce()))]);
        var phase = new SlotPhase(state.Ep, state.Id, NeedDurable: false, (channel, key) => channel.Control.ControlSyncAsync(key, record), code => code is MoveScripts.Written or MoveScripts.AlreadyWrittenOrOlder);
        var result = await RunSlotPhaseAsync(group, episode, phase, cancellationToken).ConfigureAwait(false);
        if (result.Conflict)
        {
            return new TransitionResult(TransitionOutcome.IdentityConflict, Detail: "a control record holds another identity at this ep, or a higher ep");
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

    // Steps 4 and 5. Null on success; CommitIncomplete asks for the steps again.
    private async Task<TransitionResult?> CommitStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var phase = new SlotPhase(state.Ep, state.Id, NeedDurable: true, (channel, key) => channel.Control.ControlCommitAsync(key, state.Ep, state.Id), code => code == MoveScripts.Written);
        var result = await RunSlotPhaseAsync(group, episode, phase, cancellationToken).ConfigureAwait(false);
        if (result.Conflict)
        {
            return new TransitionResult(TransitionOutcome.IdentityConflict, Detail: "a control record holds another identity at this ep, or a higher ep");
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

    // Step 6.
    private async Task<TransitionResult> DurableStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var (confirmed, _) = await StateStepAsync(episode, key, channel => channel.State.DurableAsync(key, state.Id, episode.Sequence.Release), code => code == MoveScripts.Written, cancellationToken).ConfigureAwait(false);
        if (confirmed == BarrierResult.Refused)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored id changed before DURABLE");
        }

        if (confirmed is BarrierResult.Failed or BarrierResult.Skipped)
        {
            NoteSkipped(episode, "the DURABLE write of the state");
        }

        return new TransitionResult(TransitionOutcome.Completed, episode.WaitSkipped);
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
            var current = layout;
            var misrouted = new ConcurrentDictionary<int, EndPoint>();
            var confirmations = await Task.WhenAll(pending.GroupBy(slot => owners.GetValueOrDefault(slot) ?? current.OwnerOf(slot)).Select(async owned =>
            {
                var ownedSlots = owned.ToArray();
                var result = await ConfirmAsync(
                    episode,
                    owned.Key,
                    ConfiguredReplicas(current, owned.Key),
                    channel => IssueAsync(channel, names, group, ownedSlots, phase, flags, misrouted),
                    cancellationToken).ConfigureAwait(false);
                return (owned.Key, result);
            })).ConfigureAwait(false);
            failedMasters.UnionWith(confirmations.Where(c => c.result == BarrierResult.Failed).Select(c => c.Key));
            skipped |= confirmations.Any(c => c.result == BarrierResult.Skipped);
            if (flags.Conflict || flags.Refused)
            {
                return new SlotPhaseResult(flags.Conflict, flags.Refused, 0, false, false);
            }

            var next = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            var moved = next.MovedSince(layout, slots);
            var verified = await VerifyAsync(group, episode, next, slots, phase, cancellationToken).ConfigureAwait(false);
            if (verified.Conflict)
            {
                return new SlotPhaseResult(true, false, 0, false, false);
            }

            layout = next;
            foreach (var (slot, owner) in misrouted)
            {
                owners[slot] = owner;
            }

            foreach (var slot in moved)
            {
                owners.Remove(slot);
            }

            unverified = verified.Pending;
            pending = [.. verified.Pending.Union(moved).Union(misrouted.Keys).Where(slot => !failedMasters.Contains(owners.GetValueOrDefault(slot) ?? next.OwnerOf(slot)))];
        }

        return new SlotPhaseResult(false, false, unverified.Union(pending).Count(), failedMasters.Count > 0, skipped);
    }

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
            if (held[0].IsNull || !long.TryParse((string)held[0]!, out var ep) || ep < phase.TargetEp)
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

    // The writes of one master, then WAIT on the same connection; a short count is tried again on a replaced connection.
    private async Task<BarrierResult> ConfirmAsync(Episode episode, EndPoint master, int configuredReplicas, Func<IMasterChannel, Task<IssueResult>> issue, CancellationToken cancellationToken)
    {
        if (configuredReplicas < 0)
        {
            throw new InvalidOperationException("The number of replicas of the master is not configured, so a replication barrier cannot tell a missing replica from none; set ExpectedReplicasPerMaster (0 for none).");
        }

        var required = Math.Min(options.StateWaitReplicas, configuredReplicas);
        if (required <= 0)
        {
            if (episode.NoReplica.TryAdd(master, 0))
            {
                report?.Invoke("no_replica", master.ToString()!);
            }

            var only = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
            return await issue(only).ConfigureAwait(false) switch
            {
                IssueResult.Done => BarrierResult.Confirmed,
                IssueResult.Refused => BarrierResult.Refused,
                _ => BarrierResult.Misrouted,
            };
        }

        var seq = episode.Sequence;
        if (seq.SkipAtOnce || (seq.WayBack && seq.Ledger?.FailedRecently(master) == true))
        {
            var unwaited = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
            return await issue(unwaited).ConfigureAwait(false) switch
            {
                IssueResult.Done => BarrierResult.Skipped,
                IssueResult.Refused => BarrierResult.Refused,
                _ => BarrierResult.Misrouted,
            };
        }

        for (var attempt = 0; attempt < options.BarrierRetries; attempt++)
        {
            var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
            try
            {
                var issued = await issue(channel).ConfigureAwait(false);
                if (issued != IssueResult.Done)
                {
                    return issued == IssueResult.Refused ? BarrierResult.Refused : BarrierResult.Misrouted;
                }

                if (await channel.WaitAsync(required, options.BarrierWait, cancellationToken).ConfigureAwait(false) >= required)
                {
                    return BarrierResult.Confirmed;
                }
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // A connection that failed mid-barrier is replaced and the writes are issued again; they are idempotent.
            }

            if (attempt + 1 < options.BarrierRetries)
            {
                await episode.Channels.ReplaceAsync(master, cancellationToken).ConfigureAwait(false);
            }
        }

        seq.Ledger?.Record(master);
        report?.Invoke("barrier_failed", master.ToString()!);
        return BarrierResult.Failed;
    }

    // A state-key write, the check of which master got it, and the WAIT on that master's connection; a write sent elsewhere is issued again on the owner.
    private async Task<(BarrierResult Result, long Code)> StateStepAsync(Episode episode, RedisKey key, Func<IMasterChannel, Task<long>> write, Func<long, bool> accepts, CancellationToken cancellationToken)
    {
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        var owner = layout.OwnerOf(HashSlot.Of(key.ToString()));
        long code = 0;
        for (var hop = 0; hop < options.BarrierRetries; hop++)
        {
            EndPoint? actual = null;
            var result = await ConfirmAsync(
                episode,
                owner,
                ConfiguredReplicas(layout, owner),
                async channel =>
                {
                    var before = await channel.StateOwnerAsync(key).ConfigureAwait(false);
                    code = await write(channel).ConfigureAwait(false);
                    if (!accepts(code))
                    {
                        return IssueResult.Refused;
                    }

                    var after = await channel.StateOwnerAsync(key).ConfigureAwait(false);
                    if (channel.Endpoint.Equals(before) && channel.Endpoint.Equals(after))
                    {
                        return IssueResult.Done;
                    }

                    actual = after ?? before;
                    return IssueResult.Misrouted;
                },
                cancellationToken).ConfigureAwait(false);
            if (result != BarrierResult.Misrouted)
            {
                return (result, code);
            }

            layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            owner = actual ?? layout.OwnerOf(HashSlot.Of(key.ToString()));
        }

        return (BarrierResult.Failed, code);
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

    private async Task<(EndPoint Master, int Configured)> StateMasterAsync(RedisKey key, CancellationToken cancellationToken)
    {
        var layout = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
        var master = layout.OwnerOf(HashSlot.Of(key.ToString()));
        return (master, layout.MasterAt(master).ConfiguredReplicas);
    }

    private async Task<StateRecord?> ReadStateAsync(string group, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var (master, _) = await StateMasterAsync(key, cancellationToken).ConfigureAwait(false);
        var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
        return StateRecord.Parse(await channel.StateDatabase.HashGetAllAsync(key, Master).ConfigureAwait(false));
    }

    private sealed record SlotPhase(long TargetEp, string TargetId, bool NeedDurable, Func<IMasterChannel, RedisKey, Task<long>> Issue, Func<long, bool> Accepts);

    private sealed record SlotPhaseResult(bool Conflict, bool Refused, int Unverified, bool BarrierFailed, bool BarrierSkipped);

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
        public MasterChannels Channels { get; } = new(topology);

        public SequenceOptions Sequence { get; } = sequence;

        public ConcurrentDictionary<EndPoint, byte> NoReplica { get; } = new();

        public bool WaitSkipped { get; set; }

        public ValueTask DisposeAsync() => Channels.DisposeAsync();
    }
}
