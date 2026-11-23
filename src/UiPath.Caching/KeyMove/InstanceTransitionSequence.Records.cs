using System.Globalization;
using System.Net;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class InstanceTransitionSequence
{
    private const int ReadBatch = 512;

    internal async Task<PreflightResult> DstPreflightAsync(string group, CancellationToken cancellationToken)
    {
        var failures = new List<PreflightFailure>();
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(options.DstPreflightTimeout);
        var database = conns.Barrier(RecordSide.Dst);
        try
        {
            var state = await database.HashGetAllAsync(placement.State(group).Key, Master).WaitAsync(bound.Token).ConfigureAwait(false);
            if (state.Length == 0)
            {
                failures.Add(new PreflightFailure(KeyMoveGateIds.DstStateMissing, "the Dst state hash is absent"));
            }

            var masters = database.Multiplexer.GetServers().Where(static s => s.IsConnected && !s.IsReplica).ToArray();
            if (masters.Length == 0)
            {
                failures.Add(new PreflightFailure(KeyMoveGateIds.DstUnreachable, "no Dst master is connected"));
            }

            foreach (var server in masters)
            {
                await CheckMasterAsync(server, failures, options.ExpectedReplicasPerMaster, bound.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failures.Add(new PreflightFailure(KeyMoveGateIds.DstPreflightTimeout, $"the Dst health read took longer than {options.DstPreflightTimeout}"));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            failures.Add(new PreflightFailure(KeyMoveGateIds.DstUnreachable, ex.Message));
        }

        return new PreflightResult(failures);
    }

    private static async Task CheckMasterAsync(IServer server, List<PreflightFailure> failures, int? expected, CancellationToken cancellationToken)
    {
        try
        {
            var role = await server.RoleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (role is not Role.Master master)
            {
                failures.Add(new PreflightFailure(KeyMoveGateIds.DstNotMaster, $"{server.EndPoint} is not a master"));
            }
            else if (expected is { } wanted && master.Replicas.Count < wanted)
            {
                failures.Add(new PreflightFailure(KeyMoveGateIds.DstReplicaUnhealthy, $"{server.EndPoint} has {master.Replicas.Count} of {wanted} replicas"));
            }
        }
        catch (RedisServerException)
        {
            // ROLE denied: the replicas are unknown, which the barrier's own WAIT decides.
        }
    }

    private static TransitionResult RecordConflict() =>
        new(TransitionOutcome.IdentityConflict, Detail: "a control record holds another identity at this ep, or a higher ep");

    // dst_preflight before a Src write: a failure stops the transition with nothing written to the Src.
    private async Task<TransitionResult?> PreflightGateAsync(string group, CancellationToken cancellationToken)
    {
        var preflight = await DstPreflightAsync(group, cancellationToken).ConfigureAwait(false);
        if (preflight.Ok)
        {
            return null;
        }

        var detail = string.Join("; ", preflight.Failures.Select(static f => $"{f.Code}: {f.Detail}"));
        report?.Invoke(KeyMoveGateIds.DstPreflight, detail);
        return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: $"{KeyMoveGateIds.DstPreflight} failed, nothing was written to the Src: {detail}");
    }

    // CTLSYNC to the Dst records, the Dst health read, the Src record last; then barrier 1 on both servers.
    private async Task<TransitionResult?> SyncStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var record = state.ToControlRecord([.. state.CacheIds.Select(cid => (cid, MoveScripts.NewNonce()))]);
        var phase = new Phase(state.Ep, state.Id, NeedDurable: false, key => MoveScripts.ControlSyncOp(key, record), code => code is MoveScripts.Written or MoveScripts.AlreadyWrittenOrOlder);
        return await SyncRecordsAsync(group, phase, episode, cancellationToken).ConfigureAwait(false)
            ?? await VerifySyncedRecordsAsync(group, phase, episode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransitionResult?> SyncRecordsAsync(string group, Phase phase, Episode episode, CancellationToken cancellationToken)
    {
        var dst = await WriteSideAsync(RecordSide.Dst, Keys(RecordSide.Dst, group), phase, episode).ConfigureAwait(false);
        if (dst.Conflict)
        {
            return RecordConflict();
        }

        if ((dst.Unconfirmed || dst.Refused) && Ladder(episode, new RecordWrite(BarrierOutcome.NotConfirmed, RecordWrite.NoCode, null), RecordSide.Dst, "the Dst control records") is { } dstStuck)
        {
            // The Src record is not written while the Dst did not confirm: the narrowed window never opens on an unhealthy Dst.
            return dstStuck;
        }

        // From the marker on (a resume after it included) the completion never aborts: dst_preflight no longer gates the Src write.
        if (!episode.PastMarker && await PreflightGateAsync(group, cancellationToken).ConfigureAwait(false) is { } unhealthy)
        {
            return unhealthy;
        }

        var srcWrite = await WriteSideAsync(RecordSide.Src, Keys(RecordSide.Src, group), phase, episode).ConfigureAwait(false);
        if (srcWrite.Conflict)
        {
            return RecordConflict();
        }

        if ((srcWrite.Unconfirmed || srcWrite.Refused) && Ladder(episode, new RecordWrite(BarrierOutcome.NotConfirmed, RecordWrite.NoCode, null), RecordSide.Src, "the Src control record") is { } srcStuck)
        {
            return srcStuck;
        }

        return null;
    }

    private async Task<TransitionResult?> VerifySyncedRecordsAsync(string group, Phase phase, Episode episode, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var barrier = await BarrierAsync(group, phase, episode, cancellationToken).ConfigureAwait(false);
            if (barrier.Conflict)
            {
                return RecordConflict();
            }

            if (barrier.Stop is not null)
            {
                return barrier.Stop;
            }

            if (barrier.Unverified > 0)
            {
                report?.Invoke(BarrierStuckAlert, $"{barrier.Unverified} control records do not hold the target identity");
                return new TransitionResult(TransitionOutcome.BarrierStuck, Detail: "a control record does not hold the target identity");
            }

            // A reconnect or failover of either connection since the sequence began means a record write may have been lost: both servers are verified in full once more before the claim opens.
            if (!ChangedSince(episode))
            {
                break;
            }

            episode.SourceMark = views.Changes.SourceCurrent;
            episode.DestinationMark = views.Changes.DestinationCurrent;
        }

        return null;
    }

    // CTLCOMMIT to the Dst records, then the Src; barrier 2 reads dur=1 on both servers. Null on success; CommitIncomplete asks for the steps again.
    private async Task<TransitionResult?> CommitStepAsync(string group, StateRecord state, Episode episode, CancellationToken cancellationToken)
    {
        var phase = new Phase(state.Ep, state.Id, NeedDurable: true, key => MoveScripts.ControlCommitOp(key, state.Ep, state.Id), code => code == MoveScripts.Written);
        var dst = await WriteSideAsync(RecordSide.Dst, Keys(RecordSide.Dst, group), phase, episode).ConfigureAwait(false);
        if (dst.Conflict)
        {
            return RecordConflict();
        }

        if (dst.Refused)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete);
        }

        var srcWrite = await WriteSideAsync(RecordSide.Src, Keys(RecordSide.Src, group), phase, episode).ConfigureAwait(false);
        if (srcWrite.Conflict)
        {
            return RecordConflict();
        }

        if (srcWrite.Refused)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete);
        }

        if (dst.Unconfirmed || srcWrite.Unconfirmed)
        {
            NoteSkipped(episode, "barrier 2");
        }

        return await VerifyCommittedRecordsAsync(group, phase, episode, cancellationToken).ConfigureAwait(false);
    }

    // Bracket verification with generation reads to detect a failover during the read.
    private async Task<TransitionResult?> VerifyCommittedRecordsAsync(string group, Phase phase, Episode episode, CancellationToken cancellationToken)
    {
        var before = await generations.ReadPairAsync(cancellationToken).ConfigureAwait(false);
        var barrier = await BarrierAsync(group, phase, episode, cancellationToken).ConfigureAwait(false);
        if (barrier.Conflict)
        {
            return RecordConflict();
        }

        if (barrier.Stop is not null)
        {
            return barrier.Stop;
        }

        if (barrier.Unverified > 0)
        {
            return new TransitionResult(TransitionOutcome.CommitIncomplete);
        }

        var after = await generations.ReadPairAsync(cancellationToken).ConfigureAwait(false);
        if (before is not { } held || after != held)
        {
            episode.Blocked = "a generation cannot be read or changed during the final verification";
            return new TransitionResult(TransitionOutcome.CommitIncomplete);
        }

        var final = new FinalVerification(barrier.Verified ?? [], held);
        if (!final.Covers(Keys(RecordSide.Src, group), Keys(RecordSide.Dst, group)))
        {
            episode.Blocked = "the final verification does not cover the records of both servers";
            return new TransitionResult(TransitionOutcome.CommitIncomplete);
        }

        episode.Final = final;
        Volatile.Write(ref _lastFinal, final);
        return null;
    }

    private bool ChangedSince(Episode episode) =>
        views.Changes.ChangedSince(RecordSide.Src, episode.SourceMark) || views.Changes.ChangedSince(RecordSide.Dst, episode.DestinationMark);

    private RedisKey[] Keys(RecordSide side, string group) => [.. placement.Control(side, group).Select(static r => r.Key)];

    // The records of one server, each master's records in transactions of its own with one WAIT.
    private async Task<SideWrite> WriteSideAsync(RecordSide side, RedisKey[] keys, Phase phase, Episode episode)
    {
        if (keys.Length == 0)
        {
            return new SideWrite(false, false, false);
        }

        var (groups, unresolved) = await ResolveRecordOwnersAsync(side, keys).ConfigureAwait(false);
        var results = await Task.WhenAll(groups.Select(g => g.Keys.Count == 1
            ? g.Writer.RunAsync(phase.Op(g.Keys[0]), episode.WayBack)
            : g.Writer.RunManyAsync([.. g.Keys.Select(phase.Op)], episode.WayBack))).ConfigureAwait(false);
        var conflict = results.Any(static r => r.Code == MoveScripts.IdentityConflict);
        var refused = results.Any(r => r.Code != RecordWrite.NoCode && r.Code != MoveScripts.IdentityConflict && !phase.Accepts(r.Code));
        var unconfirmed = unresolved || results.Any(static r => !r.Confirmed);
        return new SideWrite(conflict, refused, unconfirmed);
    }

    private async Task<(List<(ServerRecordWriter Writer, List<RedisKey> Keys)> Groups, bool Unresolved)> ResolveRecordOwnersAsync(RecordSide side, IReadOnlyList<RedisKey> keys)
    {
        if (side == RecordSide.Src)
        {
            return ([(src, [.. keys])], false);
        }

        var byMaster = new Dictionary<EndPoint, List<RedisKey>>();
        var database = conns.Barrier(RecordSide.Dst);
        var unresolved = false;
        foreach (var key in keys)
        {
            if (await database.IdentifyEndpointAsync(key, Master).ConfigureAwait(false) is not { } endpoint)
            {
                unresolved = true;
                continue;
            }

            if (!byMaster.TryGetValue(endpoint, out var owned))
            {
                byMaster[endpoint] = owned = [];
            }

            owned.Add(key);
        }

        return ([.. byMaster.Select(m => (dstMasters(m.Key), m.Value))], unresolved);
    }

    // Barrier 1 and 2: every record of both servers is read; one that does not hold the target is sent again on its own server (Dst first, Src last), up to BarrierRetries times.
    // A repair goes through the failure policy of the phase: before the marker an unconfirmed Dst repair stops the transition and dst_preflight must pass again before a Src repair;
    // from the marker on it is wait_skipped. A master read that shows the repair never stands in for its confirmation.
    private async Task<BarrierCheck> BarrierAsync(string group, Phase phase, Episode episode, CancellationToken cancellationToken)
    {
        // Barrier 2 runs after the commit marker, so the commit phase is past it whatever the marker write reported.
        var past = phase.NeedDurable || episode.PastMarker;
        for (var round = 0; round < options.BarrierRetries; round++)
        {
            var verified = await VerifyAsync(group, phase, cancellationToken).ConfigureAwait(false);
            if (verified.Conflict)
            {
                return new BarrierCheck(true, 0);
            }

            if (verified.Pending.Count == 0)
            {
                return new BarrierCheck(false, 0, Verified: verified.Verified);
            }

            if (await RepairPendingAsync(group, phase, episode, verified.Pending, past, cancellationToken).ConfigureAwait(false) is { } stopped)
            {
                return stopped;
            }
        }

        var last = await VerifyAsync(group, phase, cancellationToken).ConfigureAwait(false);
        return new BarrierCheck(last.Conflict, last.Pending.Count, Verified: last.Verified);
    }

    private async Task<BarrierCheck?> RepairPendingAsync(
        string group, Phase phase, Episode episode, List<(RecordSide Side, RedisKey Key)> pending, bool past, CancellationToken cancellationToken)
    {
        foreach (var side in new[] { RecordSide.Dst, RecordSide.Src })
        {
            var again = pending.Where(p => p.Side == side).Select(static p => p.Key).ToArray();
            if (again.Length == 0)
            {
                continue;
            }

            if (side == RecordSide.Src && !past && await PreflightGateAsync(group, cancellationToken).ConfigureAwait(false) is { } unhealthy)
            {
                return new BarrierCheck(false, 0, unhealthy);
            }

            var repair = await WriteSideAsync(side, again, phase, episode).ConfigureAwait(false);
            if (repair.Conflict)
            {
                return new BarrierCheck(true, 0);
            }

            if ((repair.Unconfirmed || repair.Refused) && RepairFailure(side, episode, past) is { } stuck)
            {
                return new BarrierCheck(false, 0, stuck);
            }
        }

        return null;
    }

    private TransitionResult? RepairFailure(RecordSide side, Episode episode, bool past)
    {
        var detail = $"the repair of a {side} control record";
        if (past)
        {
            NoteSkipped(episode, detail);
            return null;
        }

        return Ladder(episode, new RecordWrite(BarrierOutcome.NotConfirmed, RecordWrite.NoCode, null), side, detail);
    }

    private async Task<Verification> VerifyAsync(string group, Phase phase, CancellationToken cancellationToken)
    {
        var pending = new List<(RecordSide Side, RedisKey Key)>();
        var verified = new List<(RecordSide Side, RedisKey Key)>();
        foreach (var side in new[] { RecordSide.Dst, RecordSide.Src })
        {
            var keys = Keys(side, group);
            var read = await ReadFieldsAsync(side, keys, [StateFields.Ep, StateFields.Id, StateFields.Durable], cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < keys.Length; i++)
            {
                if (read is null)
                {
                    pending.Add((side, keys[i]));
                    continue;
                }

                var held = read[i];
                if (held[0].IsNull || !long.TryParse(held[0].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ep) || ep < phase.TargetEp)
                {
                    pending.Add((side, keys[i]));
                }
                else if (ep > phase.TargetEp || held[1] != phase.TargetId)
                {
                    return new Verification(pending, [], true);
                }
                else if (phase.NeedDurable && held[2] != "1")
                {
                    pending.Add((side, keys[i]));
                }
                else
                {
                    verified.Add((side, keys[i]));
                }
            }
        }

        return new Verification(pending, verified, false);
    }

    private async Task<bool?> AnyCommittedAsync(string group, long ep, CancellationToken cancellationToken)
    {
        foreach (var side in new[] { RecordSide.Dst, RecordSide.Src })
        {
            if (await ReadFieldsAsync(side, Keys(side, group), [StateFields.Ep, StateFields.Durable], cancellationToken).ConfigureAwait(false) is not { } read)
            {
                return null;
            }

            if (read.Any(h => h[1] == "1" && h[0] == ep))
            {
                return true;
            }
        }

        return false;
    }

    // HMGET of the fields on the master of each record, pipelined in batches; null when the server cannot be read.
    private async Task<RedisValue[][]?> ReadFieldsAsync(RecordSide side, RedisKey[] keys, RedisValue[] fields, CancellationToken cancellationToken)
    {
        var database = conns.Barrier(side);
        var all = new List<RedisValue[]>(keys.Length);
        try
        {
            foreach (var chunk in keys.Chunk(ReadBatch))
            {
                var reads = chunk.Select(key => database.HashGetAsync(key, fields, Master)).ToArray();
                all.AddRange(await Task.WhenAll(reads).WaitAsync(cancellationToken).ConfigureAwait(false));
            }

            return [.. all];
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;
        }
    }

    private sealed record Phase(long TargetEp, string TargetId, bool NeedDurable, Func<RedisKey, RecordOp> Op, Func<long, bool> Accepts);

    private sealed record SideWrite(bool Conflict, bool Refused, bool Unconfirmed);

    private sealed record BarrierCheck(bool Conflict, int Unverified, TransitionResult? Stop = null, IReadOnlyList<(RecordSide Side, RedisKey Key)>? Verified = null);

    private sealed record Verification(List<(RecordSide Side, RedisKey Key)> Pending, List<(RecordSide Side, RedisKey Key)> Verified, bool Conflict);
}
