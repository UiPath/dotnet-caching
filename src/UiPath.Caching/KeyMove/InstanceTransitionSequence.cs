using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

// Rechecking both generations narrows the race before DURABLE. The two servers cannot perform the recheck and write atomically.
internal sealed partial class InstanceTransitionSequence(
    MovePlacement placement,
    SideConnections conns,
    ServerRecordWriter dstState,
    ServerRecordWriter src,
    Func<EndPoint, ServerRecordWriter> dstMasters,
    MoveViews views,
    IGenerationSource generations,
    KeyMoveStateOptions options,
    Action<string, string>? report = null) : ITransitionSequence
{
    private const string BarrierStuckAlert = "barrier_stuck";
    private const CommandFlags Master = CommandFlags.DemandMaster;

    private FinalVerification? _lastFinal;

    internal FinalVerification? LastFinal => Volatile.Read(ref _lastFinal);

    public async Task<TransitionResult> RunAsync(string group, StateExpectation expected, StateTarget target, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        var episode = new Episode(sequence ?? new SequenceOptions(), views.Changes.SourceCurrent, views.Changes.DestinationCurrent);
        if (LeaseLost(episode) is { } lost)
        {
            return lost;
        }

        var key = placement.State(group).Key;
        var write = await dstState.RunAsync(MoveScripts.TransitOp(key, expected, target, episode.Sequence.Fence), episode.WayBack).ConfigureAwait(false);
        if (write.Code == MoveScripts.LeaseNotHeld)
        {
            return new TransitionResult(TransitionOutcome.LeaseLost, Detail: "the server found the lease held by another controller");
        }

        if (write.Code != RecordWrite.NoCode && write.Code is not (MoveScripts.Written or MoveScripts.Repeated))
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one this transition replaces");
        }

        if (!write.Confirmed)
        {
            if (write.Uncertain is not null)
            {
                // An EXEC that may have applied is never sent again: the state is read back, and a stuck transition is resumed from what it shows.
                var applied = await ReadStateAsync(group, cancellationToken).ConfigureAwait(false) is { } now && now.Id == target.Id;
                report?.Invoke(BarrierStuckAlert, $"the TRANSIT write is uncertain ({(applied ? "applied" : "not applied")}): {write.Uncertain.GetType().Name}");
            }

            if (Ladder(episode, write, RecordSide.Dst, "the TRANSIT write of the state") is { } stuck)
            {
                return stuck;
            }
        }

        return await CompleteAsync(group, episode, target.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransitionResult> AbandonAsync(string group, bool advertAcknowledges, long updatedAt, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(group, cancellationToken).ConfigureAwait(false);
        if (state is null || state.Durable || advertAcknowledges || state.Field(StateFields.CommitBegun) == state.Id)
        {
            return new TransitionResult(TransitionOutcome.AbandonRefused, Detail: "the transition is committed, acknowledged or absent");
        }

        var committed = await AnyCommittedAsync(group, state.Ep, cancellationToken).ConfigureAwait(false);
        if (committed is null)
        {
            report?.Invoke(BarrierStuckAlert, "a server could not be read for the abandon");
            return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "a server could not be read, so the abandon cannot be checked");
        }

        if (committed == true)
        {
            return new TransitionResult(TransitionOutcome.AbandonRefused, Detail: "a control record already shows dur=1 at this ep");
        }

        var prev = state.Prev.Split(':');
        var target = new StateTarget(prev[0], prev[1], int.Parse(prev[2], System.Globalization.CultureInfo.InvariantCulture), state.Ep + 1, MoveScripts.NewNonce(), state.Prev, updatedAt);
        return await RunAsync(group, StateExpectation.Holding(state.Ep, state.Id, 0, abandon: true), target, (sequence ?? new SequenceOptions()) with { SkipAtOnce = true, WayBack = true }, cancellationToken).ConfigureAwait(false);
    }

    public Task<TransitionResult> ResumeAsync(string group, SequenceOptions? sequence, CancellationToken cancellationToken)
    {
        var episode = new Episode(sequence ?? new SequenceOptions(), views.Changes.SourceCurrent, views.Changes.DestinationCurrent);
        return CompleteAsync(group, episode, null, cancellationToken);
    }

    private static TransitionResult? LeaseLost(Episode episode) =>
        episode.Sequence.HoldsLease is { } holds && !holds() ? new TransitionResult(TransitionOutcome.LeaseLost, Detail: "the controller no longer holds the lease") : null;

    private async Task<TransitionResult> CompleteAsync(string group, Episode episode, string? expectedId, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(group, cancellationToken).ConfigureAwait(false);
        if (state is null || (expectedId is not null && state.Id != expectedId))
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored state is not the one being completed");
        }

        // A stored commit marker for this identity is the line of no abandon: the episode starts past it, so no completion step aborts.
        if (state.Field(StateFields.CommitBegun) == state.Id)
        {
            episode.PastMarker = true;
        }

        // A durable state may be one whose DURABLE reply or WAIT was lost: the write and its WAIT are repeated, never trusted.
        if (state.Durable)
        {
            episode.PastMarker = true;
            return LeaseLost(episode) ?? await DurableStepAsync(group, state, episode).ConfigureAwait(false);
        }

        return await VerifyAndPublishAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransitionResult> VerifyAndPublishAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var rechecked = false;
        for (var round = 0; round < options.BarrierRetries && !rechecked; round++)
        {
            episode.Final = null;
            if (await CommitRecordsAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } blocked)
            {
                return blocked;
            }

            if (episode.Sequence.BeforeDurable is { } beforeDurable && await beforeDurable(cancellationToken).ConfigureAwait(false) is { } halted)
            {
                return halted;
            }

            // The recheck is a step of its own, just before DURABLE: a generation that differs or cannot be read means the verification may no longer hold.
            rechecked = await RecheckAsync(episode, cancellationToken).ConfigureAwait(false);
            if (!rechecked)
            {
                episode.Blocked = "a generation changed or cannot be read since the final verification";
            }
        }

        if (!rechecked)
        {
            return Blocked(episode);
        }

        if (LeaseLost(episode) is { } beforeDurableWrite)
        {
            return beforeDurableWrite;
        }

        return await DurableStepAsync(group, state, episode).ConfigureAwait(false);
    }

    private async Task<TransitionResult?> CommitRecordsAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < options.BarrierRetries; attempt++)
        {
            episode.Blocked = null;
            if (await StartCommitAsync(group, state, episode, cancellationToken).ConfigureAwait(false) is { } stopped)
            {
                return stopped;
            }

            var result = await CommitStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Outcome != TransitionOutcome.CommitIncomplete)
            {
                return result;
            }
        }

        return Blocked(episode);
    }

    private async Task<TransitionResult?> StartCommitAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
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

        if (episode.Sequence.BeforeCommit is { } hook && await hook(cancellationToken).ConfigureAwait(false) is { } stopped)
        {
            return stopped;
        }

        return await CommitBeginStepAsync(group, state, episode, cancellationToken).ConfigureAwait(false);
    }

    // A resumable stop, reported once: the stop of a sequence that is blocked is never Completed, and past the marker never an abort.
    private TransitionResult Blocked(Episode episode)
    {
        if (episode.Blocked is not { } why)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete, Detail: "a control record would not take the commit marker");
        }

        report?.Invoke(BarrierStuckAlert, why);
        return new TransitionResult(TransitionOutcome.CommitIncomplete, Detail: why);
    }

    // Both generations now, against the pair the final verification recorded; unknown is a mismatch.
    private async Task<bool> RecheckAsync(Episode episode, CancellationToken cancellationToken) =>
        episode.Final is { } final && await generations.ReadPairAsync(cancellationToken).ConfigureAwait(false) is { } now && now == final.Generations;

    private async Task<TransitionResult?> CommitBeginStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var key = placement.State(group).Key;
        var write = await dstState.RunAsync(MoveScripts.CommitBeginOp(key, state.Id, episode.Sequence.Fence), episode.WayBack).ConfigureAwait(false);
        if (write.Code == MoveScripts.LeaseNotHeld)
        {
            return new TransitionResult(TransitionOutcome.LeaseLost, Detail: "the server found the lease held by another controller");
        }

        if (write.Code != RecordWrite.NoCode && write.Code != MoveScripts.Written)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the identity was replaced before its commit began");
        }

        if (!write.Confirmed && Ladder(episode, write, RecordSide.Dst, "the commit marker") is { } stuck)
        {
            return stuck;
        }

        // Confirmed, or skipped by a way-back or an operator: the marker is written, so an abandon of this identity is refused from here on.
        episode.PastMarker = write.Code == MoveScripts.Written || episode.PastMarker;

        // The guard of the caller sees the generation the marker was written in; the marker stays, so the stop is resumable and the next attempt repairs and writes it again.
        return episode.Sequence.AfterMarker is { } guard ? await guard(write.Proof, cancellationToken).ConfigureAwait(false) : null;
    }

    private async Task<TransitionResult> DurableStepAsync(string group, StateRecord state, Episode episode)
    {
        var key = placement.State(group).Key;
        var write = await dstState.RunAsync(MoveScripts.DurableOp(key, state.Id, episode.Sequence.Release), episode.WayBack).ConfigureAwait(false);
        if (write.Code != RecordWrite.NoCode && write.Code != MoveScripts.Written)
        {
            return new TransitionResult(TransitionOutcome.StateChanged, Detail: "the stored id changed before DURABLE");
        }

        if (write.Confirmed && write.Code == MoveScripts.Written)
        {
            return new TransitionResult(TransitionOutcome.Completed, episode.WaitSkipped);
        }

        // Not confirmed, or confirmed with no code to show it applied: the identity, dur and the release certificate are read back. Only a state that shows all of them
        // is complete (an unconfirmed WAIT after an applied write is wait_skipped); anything else is resumable, and never reported as done.
        if (await DurableShownAsync(group, state.Id, episode.Sequence.Release).ConfigureAwait(false) is not true)
        {
            report?.Invoke(BarrierStuckAlert, "the DURABLE write of the state is not proven");
            return new TransitionResult(TransitionOutcome.CommitIncomplete, Detail: "the DURABLE write is not proven applied; ResumeAsync repeats it");
        }

        if (!write.Confirmed)
        {
            NoteSkipped(episode, "the DURABLE write of the state");
        }

        return new TransitionResult(TransitionOutcome.Completed, episode.WaitSkipped);
    }

    // Whether the Dst state shows this identity as durable with the release certificate DURABLE writes; null when it cannot be read.
    private async Task<bool?> DurableShownAsync(string group, string id, string release)
    {
        try
        {
            var read = StateRecord.Parse(await conns.Barrier(RecordSide.Dst).HashGetAllAsync(placement.State(group).Key, Master).ConfigureAwait(false));
            return read is not null && read.Id == id && read.Durable && (release.Length == 0 || read.Release == release);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }

    private Task<StateRecord?> ReadStateAsync(string group, CancellationToken cancellationToken)
    {
        var (_, key) = placement.State(group);
        return ReadAsync();

        async Task<StateRecord?> ReadAsync() =>
            StateRecord.Parse(await conns.Barrier(RecordSide.Dst).HashGetAllAsync(key, Master).WaitAsync(cancellationToken).ConfigureAwait(false));
    }

    private void NoteSkipped(Episode episode, string what)
    {
        episode.WaitSkipped = true;
        report?.Invoke("wait_skipped", what);
    }

    // Confirmed or NoReplica go on. Anything else before the marker waits (barrier_stuck), unless the sequence is a way-back one or skips barriers; from the marker on it is wait_skipped.
    private TransitionResult? Ladder(Episode episode, RecordWrite write, RecordSide side, string what)
    {
        if (write.Confirmed)
        {
            return null;
        }

        if (episode.PastMarker || episode.Sequence.SkipBarriers || episode.Sequence.WayBack)
        {
            NoteSkipped(episode, what);
            return null;
        }

        report?.Invoke(BarrierStuckAlert, what);
        report?.Invoke("server_unreachable", side.ToString().ToLowerInvariant());
        return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: $"{what} is not on a replica of the {side} server");
    }

    private sealed class Episode(SequenceOptions sequence, long sourceMark, long destinationMark)
    {
        public SequenceOptions Sequence { get; } = sequence;

        public long SourceMark { get; set; } = sourceMark;

        public long DestinationMark { get; set; } = destinationMark;

        public bool WaitSkipped { get; set; }

        /// <summary>The final verification of the commit attempt in progress; null until one has covered both servers under unchanged generations.</summary>
        public FinalVerification? Final { get; set; }

        /// <summary>Why the last commit attempt could not reach the recheck, for the one alert of a sequence that stops blocked.</summary>
        public string? Blocked { get; set; }

        /// <summary>The commit marker is confirmed or this is a completion: from here on nothing aborts.</summary>
        public bool PastMarker { get; set; }

        public bool WayBack => Sequence.WayBack || Sequence.SkipAtOnce;
    }
}
