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
        var key = names.State(group);
        var (master, configured) = await StateMasterAsync(key, cancellationToken).ConfigureAwait(false);
        var confirmed = await ConfirmAsync(
            episode,
            master,
            configured,
            async channel => await channel.State.TransitAsync(key, expected, target).ConfigureAwait(false) != MoveScripts.ExpectationFailed,
            cancellationToken).ConfigureAwait(false);
        if (confirmed == BarrierResult.Refused)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one this transition replaces");
        }

        if (confirmed == BarrierResult.Failed && !SkipOrStuck(episode, "the TRANSIT write of the state"))
        {
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "the state write is not on a replica");
        }

        return await CompleteAsync(group, episode, target.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Steps 2 to 6 for the transition the stored state shows at <c>dur=0</c>; nothing to do for a state that is already durable.</summary>
    public async Task<TransitionResult> ResumeAsync(string group, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        await using var episode = new Episode(topology, sequence ?? new SequenceOptions());
        return await CompleteAsync(group, episode, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IssueAsync(IMasterChannel channel, MoveKeyNames names, string group, int[] slots, SlotPhase phase, PhaseFlags flags)
    {
        foreach (var chunk in slots.Chunk(SlotBatch))
        {
            var codes = await Task.WhenAll(chunk.Select(slot => phase.Issue(channel, names.Control(group, slot)))).ConfigureAwait(false);
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
                return false;
            }
        }

        return true;
    }

    private async Task<TransitionResult> CompleteAsync(string group, Episode episode, string? expectedId, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(group, episode, cancellationToken).ConfigureAwait(false);
        if (state is null || (expectedId is not null && state.Id != expectedId))
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one being completed");
        }

        if (state.Durable)
        {
            return new TransitionResult(TransitionOutcome.Completed);
        }

        var committed = false;
        for (var attempt = 0; attempt < options.BarrierRetries && !committed; attempt++)
        {
            if (await SyncStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } syncFailure)
            {
                return syncFailure;
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

        return await DurableStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
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

        if (result.BarrierFailed)
        {
            episode.WaitSkipped = true;
            report?.Invoke("wait_skipped", "barrier 2");
        }

        return null;
    }

    // Step 6.
    private async Task<TransitionResult> DurableStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var key = names.State(group);
        var (master, configured) = await StateMasterAsync(key, cancellationToken).ConfigureAwait(false);
        var confirmed = await ConfirmAsync(
            episode,
            master,
            configured,
            async channel => await channel.State.DurableAsync(key, state.Id, episode.Sequence.Release).ConfigureAwait(false) == MoveScripts.Written,
            cancellationToken).ConfigureAwait(false);
        if (confirmed == BarrierResult.Refused)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored id changed before DURABLE");
        }

        if (confirmed == BarrierResult.Failed)
        {
            episode.WaitSkipped = true;
            report?.Invoke("wait_skipped", "the DURABLE write of the state");
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
        var flags = new PhaseFlags();
        for (var round = 0; round < options.BarrierRetries && pending.Count > 0; round++)
        {
            var current = layout;
            var confirmations = await Task.WhenAll(pending.GroupBy(slot => current.OwnerOf(slot)).Select(async owned =>
            {
                var ownedSlots = owned.ToArray();
                var result = await ConfirmAsync(
                    episode,
                    owned.Key,
                    current.MasterAt(owned.Key).ConfiguredReplicas,
                    channel => IssueAsync(channel, names, group, ownedSlots, phase, flags),
                    cancellationToken).ConfigureAwait(false);
                return (owned.Key, result);
            })).ConfigureAwait(false);
            failedMasters.UnionWith(confirmations.Where(c => c.result == BarrierResult.Failed).Select(c => c.Key));
            if (flags.Conflict || flags.Refused)
            {
                return new SlotPhaseResult(flags.Conflict, flags.Refused, 0, false);
            }

            var next = await topology.ReadLayoutAsync(cancellationToken).ConfigureAwait(false);
            var moved = next.MovedSince(layout, slots);
            var verified = await VerifyAsync(group, episode, next, slots, phase, cancellationToken).ConfigureAwait(false);
            if (verified.Conflict)
            {
                return new SlotPhaseResult(true, false, 0, false);
            }

            layout = next;
            unverified = verified.Pending;
            pending = [.. verified.Pending.Union(moved).Where(slot => !failedMasters.Contains(next.OwnerOf(slot)))];
        }

        return new SlotPhaseResult(false, false, unverified.Union(pending).Count(), failedMasters.Count > 0);
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
    private async Task<BarrierResult> ConfirmAsync(Episode episode, EndPoint master, int configuredReplicas, Func<IMasterChannel, Task<bool>> issue, CancellationToken cancellationToken)
    {
        var required = Math.Min(options.StateWaitReplicas, configuredReplicas);
        if (required <= 0)
        {
            if (episode.NoReplica.TryAdd(master, 0))
            {
                report?.Invoke("no_replica", master.ToString()!);
            }

            var only = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
            return await issue(only).ConfigureAwait(false) ? BarrierResult.Confirmed : BarrierResult.Refused;
        }

        for (var attempt = 0; attempt < options.BarrierRetries; attempt++)
        {
            var channel = await episode.Channels.GetAsync(master, cancellationToken).ConfigureAwait(false);
            try
            {
                if (!await issue(channel).ConfigureAwait(false))
                {
                    return BarrierResult.Refused;
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

        report?.Invoke("barrier_failed", master.ToString()!);
        return BarrierResult.Failed;
    }

    private bool SkipOrStuck(Episode episode, string what)
    {
        if (episode.Sequence.SkipBarriers)
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

    private sealed record SlotPhaseResult(bool Conflict, bool Refused, int Unverified, bool BarrierFailed);

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
