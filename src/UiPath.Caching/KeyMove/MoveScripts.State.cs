using System.Globalization;
using StackExchange.Redis;

namespace UiPath.Caching.KeyMove;

internal sealed partial class MoveScripts
{
    public const long Repeated = 2;
    public const long StateCreated = 1;
    public const long StateRefreshed = 2;
    public const long StateNotInitial = -1;
    public const long LeaseNotHeld = -7;
    public const long GroupStarted = -8;

    private const string TransitScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[1] then return -7 end
        local ep = redis.call('HGET', KEYS[1], 'ep')
        local id = redis.call('HGET', KEYS[1], 'id')
        if ep and tonumber(ep) == tonumber(ARGV[9]) and id == ARGV[10] then
          redis.call('HSET', KEYS[1], 'id', ARGV[10])
          return 2
        end
        if ARGV[2] == 'c' then
          if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        elseif ep ~= ARGV[3] or id ~= ARGV[4] or redis.call('HGET', KEYS[1], 'dur') ~= ARGV[5] then
          return 0
        elseif ARGV[13] == '1' and redis.call('HGET', KEYS[1], 'cmt') == id then
          return 0
        end
        redis.call('HSET', KEYS[1], 'st', ARGV[6], 'dir', ARGV[7], 'nPop', ARGV[8], 'ep', ARGV[9], 'id', ARGV[10], 'dur', '0', 'prev', ARGV[11], 'upd', ARGV[12])
        local n = tonumber(ARGV[14])
        for i = 15, 14 + n do redis.call('HDEL', KEYS[1], ARGV[i]) end
        for i = 15 + n, #ARGV, 2 do redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 1]) end
        return 1
        """;

    private const string DurableScript = """
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return 0 end
        local v = redis.call('HMGET', KEYS[1], 'st', 'dir', 'nPop', 'ep')
        if (not v[1]) or (not v[2]) or (not v[3]) or (not v[4]) then return 0 end
        redis.call('HSET', KEYS[1], 'dur', '1', 'prev', v[1] .. ':' .. v[2] .. ':' .. v[3] .. ':' .. v[4] .. ':' .. ARGV[1])
        local fg = redis.call('HGET', KEYS[1], 'fg')
        if fg then redis.call('HSET', KEYS[1], 'pfg', fg) else redis.call('HDEL', KEYS[1], 'pfg') end
        local rg = redis.call('HGET', KEYS[1], 'rg')
        if rg then redis.call('HSET', KEYS[1], 'prg', rg) else redis.call('HDEL', KEYS[1], 'prg') end
        if ARGV[2] ~= '' then redis.call('HSET', KEYS[1], 'rel', ARGV[2]) end
        return 1
        """;

    private const string CommitBeginScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[2] then return -7 end
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return 0 end
        redis.call('HSET', KEYS[1], 'cmt', ARGV[1])
        return 1
        """;

    private const string AllAckScript = """
        if #KEYS > 1 and redis.call('GET', KEYS[2]) ~= ARGV[2] then return -7 end
        if redis.call('HGET', KEYS[1], 'id') ~= ARGV[1] then return 0 end
        if ARGV[3] ~= '' and redis.call('HGET', KEYS[1], 'allAck') ~= ARGV[1] then redis.call('HSET', KEYS[1], 's3AckAt', ARGV[3]) end
        redis.call('HSET', KEYS[1], 'allAck', ARGV[1])
        return 1
        """;

    private const string InitScript = $$"""
        if redis.call('EXISTS', KEYS[1]) == 0 then
          redis.call('HSET', KEYS[1], 'st', '{{KeyMoveStates.NotStarted}}', 'dir', 'none', 'ep', ARGV[1], 'id', ARGV[2], 'dur', '1', 'nPop', '0',
            'prev', '{{KeyMoveStates.NotStarted}}:none:0:' .. ARGV[1] .. ':' .. ARGV[2], 'caches', ARGV[3], 'cs', ARGV[4], 'upd', ARGV[5])
          return 1
        end
        if redis.call('HGET', KEYS[1], 'st') == '{{KeyMoveStates.NotStarted}}' then
          redis.call('HSET', KEYS[1], 'caches', ARGV[3], 'cs', ARGV[4], 'upd', ARGV[5])
          return 2
        end
        return -1
        """;

    /// <summary>TRANSIT: a compare-and-set that replaces the stored identity with the target one at <c>dur=0</c>, or creates the hash when the expectation is absence.</summary>
    /// <returns>1 written; 0 when the stored state is not the expected one; 2 for a repeat of the stored target, which rewrites its id; <see cref="LeaseNotHeld"/> when the fence's lease is not the caller's.</returns>
    public async Task<long> TransitAsync(RedisKey state, StateExpectation expected, StateTarget target, LeaseFence? fence = null) =>
        await TransitWrite(state, expected, target, fence).RunAsync().ConfigureAwait(false);

    /// <summary>TRANSIT as a write that runs alone or inside the transaction of a barrier; see <see cref="TransitAsync"/> for its codes.</summary>
    public Write TransitWrite(RedisKey state, StateExpectation expected, StateTarget target, LeaseFence? fence = null)
    {
        var (args, optional, clear) = TransitArguments(expected, target, fence);
        return new Write(this, TransitScript, KeysOf(state, fence), args, () => PlanTransitAsync(state, expected, target, fence, clear, optional));
    }

    /// <summary>DURABLE: marks the stored identity committed and copies it into <c>prev</c>; a repeat writes both again, so a following WAIT has an offset to confirm.</summary>
    /// <param name="release">The claim certificate <c>rel</c>, written in the same step for a not-started target of a group that holds a claim; empty for none.</param>
    /// <returns>1 written (also for a repeat); 0 when the stored id differs or the hash is unreadable.</returns>
    public async Task<long> DurableAsync(RedisKey state, string id, string release = "") =>
        await DurableWrite(state, id, release).RunAsync().ConfigureAwait(false);

    /// <summary>DURABLE as a write that runs alone or inside the transaction of a barrier; see <see cref="DurableAsync"/> for its codes.</summary>
    public Write DurableWrite(RedisKey state, string id, string release = "") =>
        new(this, DurableScript, [state], [id, release], async () =>
        {
            var held = await _db.HashGetAsync(state, [StateFields.Id, StateFields.State, StateFields.Direction, StateFields.NPop, StateFields.Ep], Master).ConfigureAwait(false);
            if (held[0] != id || held.Skip(1).Any(v => v.IsNull))
            {
                return new FallbackPlan(ExpectationFailed, null);
            }

            var generations = await _db.HashGetAsync(state, [StateFields.Fg, StateFields.Rg], Master).ConfigureAwait(false);

            return new FallbackPlan(Written, (transaction, queued) =>
            {
                foreach (var (field, value) in new[] { StateFields.Id, StateFields.State, StateFields.Direction, StateFields.NPop, StateFields.Ep }.Zip(held))
                {
                    transaction.AddCondition(Condition.HashEqual(state, field, value));
                }

                var entries = new List<HashEntry> { new(StateFields.Durable, "1"), new(StateFields.Prev, $"{held[1]}:{held[2]}:{held[3]}:{held[4]}:{id}") };

                // The generations of the identity that is now committed are what an abandon of the next transition restores.
                foreach (var (field, previous, value) in new[] { (StateFields.Fg, StateFields.PrevFg, generations[0]), (StateFields.Rg, StateFields.PrevRg, generations[1]) })
                {
                    transaction.AddCondition(value.IsNull ? Condition.HashNotExists(state, field) : Condition.HashEqual(state, field, value));
                    if (value.IsNull)
                    {
                        queued.Add(transaction.HashDeleteAsync(state, previous, Master));
                    }
                    else
                    {
                        entries.Add(new HashEntry(previous, value));
                    }
                }

                if (release.Length > 0)
                {
                    entries.Add(new HashEntry(StateFields.Release, release));
                }

                queued.Add(transaction.HashSetAsync(state, [.. entries], Master));
            });
        });

    /// <summary>CMTBEGIN: marks on the state that the commit of this identity begins, before the first CTLCOMMIT. An abandon is refused from then on, and a begin is refused once the id is replaced, so the two are arbitrated by the one key both write. A repeat writes the marker again.</summary>
    /// <returns>1 marked; 0 when the stored id differs; <see cref="LeaseNotHeld"/> when the fence's lease is not the caller's.</returns>
    public async Task<long> CommitBeginAsync(RedisKey state, string id, LeaseFence? fence = null) =>
        await CommitBeginWrite(state, id, fence).RunAsync().ConfigureAwait(false);

    /// <summary>CMTBEGIN as a write that runs alone or inside the transaction of a barrier; see <see cref="CommitBeginAsync"/> for its codes.</summary>
    public Write CommitBeginWrite(RedisKey state, string id, LeaseFence? fence = null) =>
        new(this, CommitBeginScript, KeysOf(state, fence), [id, fence?.Token ?? string.Empty], async () =>
        {
            if (fence is not null && !await HoldsFenceAsync(fence).ConfigureAwait(false))
            {
                return new FallbackPlan(LeaseNotHeld, null);
            }

            if (await _db.HashGetAsync(state, StateFields.Id, Master).ConfigureAwait(false) != id)
            {
                return new FallbackPlan(ExpectationFailed, null);
            }

            return new FallbackPlan(Written, (transaction, queued) =>
            {
                AddFence(transaction, fence);
                transaction.AddCondition(Condition.HashEqual(state, StateFields.Id, id));
                queued.Add(transaction.HashSetAsync(state, StateFields.CommitBegun, id, When.Always, Master));
            });
        });

    /// <summary>ALLACK: records that every fresh node acknowledged the identity, only while it is the stored one; a repeat writes the same value again.</summary>
    /// <param name="t3At">The time of the first acknowledgement of a moving identity, written in the same step so it is never missing next to <c>allAck</c>; null for any other state.</param>
    /// <returns>1 written; 0 when the stored id differs; <see cref="LeaseNotHeld"/> when the fence's lease is not the caller's.</returns>
    public async Task<long> AllAckAsync(RedisKey state, string id, LeaseFence? fence = null, long? t3At = null)
    {
        var stamp = t3At?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var reply = await EvaluateAsync(AllAckScript, KeysOf(state, fence), [id, fence?.Token ?? string.Empty, stamp]).ConfigureAwait(false);
        if (reply is not null)
        {
            return (long)reply;
        }

        return await WatchedAsync(async () =>
        {
            if (fence is not null && !await HoldsFenceAsync(fence).ConfigureAwait(false))
            {
                return LeaseNotHeld;
            }

            var held = await _db.HashGetAsync(state, [StateFields.Id, StateFields.AllAck], Master).ConfigureAwait(false);
            if (held[0] != id)
            {
                return ExpectationFailed;
            }

            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            AddFence(transaction, fence);
            transaction.AddCondition(Condition.HashEqual(state, StateFields.Id, id));
            transaction.AddCondition(held[1].IsNull ? Condition.HashNotExists(state, StateFields.AllAck) : Condition.HashEqual(state, StateFields.AllAck, held[1]));
            if (stamp.Length > 0 && held[1] != id)
            {
                queued.Add(transaction.HashSetAsync(state, StateFields.T3At, stamp, When.Always, Master));
            }

            queued.Add(transaction.HashSetAsync(state, StateFields.AllAck, id, When.Always, Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? Written : null;
        }).ConfigureAwait(false);
    }

    /// <summary>X0: creates the not-started state above the server clock and <paramref name="highestRecordEp"/>, or refreshes the set of a not-started state; any later stage is refused.</summary>
    public async Task<InitResult> InitAsync(RedisKey state, string caches, string cacheSet, long highestRecordEp, long updatedAt)
    {
        var micros = await ServerMicrosecondsAsync(state).ConfigureAwait(false);
        var ep = Math.Max(micros, highestRecordEp + 1);
        var id = NewNonce();
        var reply = await EvaluateAsync(InitScript, [state], [ep, id, caches, cacheSet, updatedAt]).ConfigureAwait(false);
        if (reply is not null)
        {
            return new InitResult((long)reply, ep, id);
        }

        var code = await WatchedAsync(async () =>
        {
            var exists = await _db.KeyExistsAsync(state, Master).ConfigureAwait(false);
            var stage = exists ? await _db.HashGetAsync(state, StateFields.State, Master).ConfigureAwait(false) : default;
            var transaction = _db.CreateTransaction();
            var queued = new List<Task>();
            if (!exists)
            {
                transaction.AddCondition(Condition.KeyNotExists(state));
                QueueInitialState(transaction, state, caches, cacheSet, updatedAt, ep, id, queued);
                return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? StateCreated : null;
            }

            if (stage != KeyMoveStates.NotStarted)
            {
                return StateNotInitial;
            }

            transaction.AddCondition(Condition.HashEqual(state, StateFields.State, KeyMoveStates.NotStarted));
            queued.Add(transaction.HashSetAsync(state, [new(StateFields.Caches, caches), new(StateFields.CacheSet, cacheSet), new(StateFields.Updated, updatedAt)], Master));
            return await ExecuteAllAsync(transaction, queued).ConfigureAwait(false) ? StateRefreshed : null;
        }).ConfigureAwait(false);
        return new InitResult(code, ep, id);
    }

    internal async Task<long> ServerMicrosecondsAsync(RedisKey key)
    {
        var endpoint = await _db.IdentifyEndpointAsync(key, Master).ConfigureAwait(false)
            ?? throw new RedisException($"No server holds the slot of '{key}'.");
        var time = await _db.Multiplexer.GetServer(endpoint).TimeAsync(Master).ConfigureAwait(false);
        return (DateTime.SpecifyKind(time, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks / 10;
    }

    private static List<(string Field, RedisValue Value)> TransitFields(StateTarget target)
    {
        var optional = new List<(string Field, RedisValue Value)>();
        if (target.MoveId is { } moveId)
        {
            optional.Add((StateFields.MoveId, moveId));
        }

        if (target.Claim is { } claim)
        {
            optional.Add((StateFields.Claim, claim));
        }

        if (target.Caches is { } caches)
        {
            optional.Add((StateFields.Caches, caches.Caches));
            optional.Add((StateFields.CacheSet, caches.CacheSet));
        }

        if (target.Move is { } move)
        {
            optional.Add((StateFields.Move, move));
        }

        if (target.Release is { } release)
        {
            optional.Add((StateFields.Release, release));
        }

        if (target.HoldOp is { } hold)
        {
            optional.Add((StateFields.HoldOp, hold));
        }

        foreach (var (field, value) in target.Set ?? [])
        {
            optional.Add((field, value));
        }

        return optional;
    }

    private static (RedisValue[] Args, List<(string Field, RedisValue Value)> Optional, IReadOnlyList<string> Clear) TransitArguments(
        StateExpectation expected, StateTarget target, LeaseFence? fence)
    {
        if (expected.Ep is { } expectedEp && target.Ep <= expectedEp)
        {
            throw new ArgumentException($"A transition raises the epoch: {target.Ep} does not exceed {expectedEp}.", nameof(target));
        }

        var optional = TransitFields(target);
        var clear = target.Clear ?? [];
        var args = new List<RedisValue>
        {
            fence?.Token ?? string.Empty, expected.IsAbsent ? "c" : "u", expected.Ep ?? 0, expected.Id ?? string.Empty, expected.Durable,
            target.State, target.Direction, target.NPop, target.Ep, target.Id, target.Prev, target.UpdatedAt, expected.Abandon ? 1 : 0, clear.Count,
        };
        args.AddRange(clear.Select(f => (RedisValue)f));
        foreach (var (field, value) in optional)
        {
            args.Add(field);
            args.Add(value);
        }

        return ([.. args], optional, clear);
    }

    private static bool IsRepeatedTransit(RedisValue[] held, StateTarget target) =>
        !held[0].IsNull && long.TryParse((string?)held[0], NumberStyles.None, CultureInfo.InvariantCulture, out var stored) && stored == target.Ep && held[1] == target.Id;

    private static bool MatchesTransitExpectation(StateExpectation expected, RedisValue[] held) =>
        held[0] == expected.Ep && held[1] == expected.Id && held[2] == expected.Durable && !(expected.Abandon && held[3] == expected.Id);

    private static void QueueTransitConditions(ITransaction transaction, RedisKey state, LeaseFence? fence, bool absent, RedisValue[] held)
    {
        AddFence(transaction, fence);
        if (absent)
        {
            transaction.AddCondition(Condition.KeyNotExists(state));
            return;
        }

        AddIdentityConditions(transaction, state, held[0], held[1]);
        transaction.AddCondition(Condition.HashEqual(state, StateFields.Durable, held[2]));

        // Watch the commit marker so a concurrent commit begin prevents abandonment.
        transaction.AddCondition(held[3].IsNull ? Condition.HashNotExists(state, StateFields.CommitBegun) : Condition.HashEqual(state, StateFields.CommitBegun, held[3]));
    }

    private static RedisKey[] KeysOf(RedisKey state, LeaseFence? fence) => fence is null ? [state] : [state, fence.Key];

    private static void AddFence(ITransaction transaction, LeaseFence? fence)
    {
        if (fence is not null)
        {
            transaction.AddCondition(Condition.StringEqual(fence.Key, fence.Token));
        }
    }

    private static void QueueInitialState(ITransaction transaction, RedisKey state, string caches, string cacheSet, long updatedAt, long ep, string id, List<Task> queued)
    {
        queued.Add(transaction.HashSetAsync(
            state,
            [
                new(StateFields.State, KeyMoveStates.NotStarted), new(StateFields.Direction, "none"), new(StateFields.Ep, ep), new(StateFields.Id, id), new(StateFields.Durable, "1"),
                new(StateFields.NPop, "0"), new(StateFields.Prev, StateRecord.FormatPrev(KeyMoveStates.NotStarted, "none", 0, ep, id)), new(StateFields.Caches, caches),
                new(StateFields.CacheSet, cacheSet), new(StateFields.Updated, updatedAt),
            ],
            Master));
    }

    private static void QueueTransitFields(
        ITransaction transaction,
        RedisKey state,
        StateTarget target,
        IReadOnlyList<string> clear,
        List<(string Field, RedisValue Value)> optional,
        List<Task> queued)
    {
        var entries = new List<HashEntry>
        {
            new(StateFields.State, target.State), new(StateFields.Direction, target.Direction), new(StateFields.NPop, target.NPop),
            new(StateFields.Ep, target.Ep), new(StateFields.Id, target.Id), new(StateFields.Durable, "0"), new(StateFields.Prev, target.Prev),
            new(StateFields.Updated, target.UpdatedAt),
        };
        entries.AddRange(optional.Select(o => new HashEntry(o.Field, o.Value)));
        if (clear.Count > 0)
        {
            queued.Add(transaction.HashDeleteAsync(state, [.. clear.Where(f => optional.All(o => o.Field != f)).Select(f => (RedisValue)f)], Master));
        }

        queued.Add(transaction.HashSetAsync(state, [.. entries], Master));
    }

    private async Task<FallbackPlan> PlanTransitAsync(
        RedisKey state,
        StateExpectation expected,
        StateTarget target,
        LeaseFence? fence,
        IReadOnlyList<string> clear,
        List<(string Field, RedisValue Value)> optional)
    {
        if (fence is not null && !await HoldsFenceAsync(fence).ConfigureAwait(false))
        {
            return new FallbackPlan(LeaseNotHeld, null);
        }

        var held = await _db.HashGetAsync(state, [StateFields.Ep, StateFields.Id, StateFields.Durable, StateFields.CommitBegun], Master).ConfigureAwait(false);
        if (IsRepeatedTransit(held, target))
        {
            return new FallbackPlan(Repeated, (transaction, queued) =>
            {
                AddFence(transaction, fence);
                AddIdentityConditions(transaction, state, held[0], held[1]);
                queued.Add(transaction.HashSetAsync(state, StateFields.Id, target.Id, When.Always, Master));
            });
        }

        var absent = expected.IsAbsent;
        if (absent)
        {
            if (await _db.KeyExistsAsync(state, Master).ConfigureAwait(false))
            {
                return new FallbackPlan(ExpectationFailed, null);
            }
        }
        else if (!MatchesTransitExpectation(expected, held))
        {
            return new FallbackPlan(ExpectationFailed, null);
        }

        return new FallbackPlan(Written, (transaction, queued) =>
        {
            QueueTransitConditions(transaction, state, fence, absent, held);
            QueueTransitFields(transaction, state, target, clear, optional, queued);
        });
    }

    private async Task<bool> HoldsFenceAsync(LeaseFence fence) => await _db.StringGetAsync(fence.Key, Master).ConfigureAwait(false) == fence.Token;
}
