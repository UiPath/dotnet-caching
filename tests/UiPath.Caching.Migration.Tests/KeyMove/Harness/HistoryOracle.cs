using System.Collections.Immutable;

// The history checks from the interleaving harness, applied without the model world.

namespace UiPath.Caching.Migration.Tests.KeyMove.Harness;

// Records each key's mutations and reads in real time and checks the history for invariant violations.
// Only the order of events matters, so the history is a list of events and two worlds with the same order are the same state.
internal sealed class HistoryOracle
{
    private const byte MutationStart = 0;
    private const byte MutationEnd = 1;
    private const byte ReadStart = 2;
    private const byte Void = 4;
    private const byte Disturbance = 5;
    private const byte Abort = 6;

    private readonly ImmutableArray<OracleKey> _keys;
    private readonly KeyLog[] _logs;

    public HistoryOracle(ImmutableArray<OracleKey> keys)
    {
        _keys = keys;
        _logs = new KeyLog[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            _logs[i] = new KeyLog();
            foreach (var initial in new[] { keys[i].InitialL, keys[i].InitialN })
            {
                if (initial is not null)
                {
                    EndMutation(i, BeginMutation(i, initial));
                }
            }

            if (keys[i].InitialL is null && keys[i].InitialN is null)
            {
                EndMutation(i, BeginMutation(i, null));
            }
        }
    }

    public int BeginMutation(int key, string? value)
    {
        var log = _logs[key];
        log.Values.Add(value);
        log.Events.Add((MutationStart, log.Values.Count - 1));
        return log.Values.Count - 1;
    }

    public void EndMutation(int key, int id) => _logs[key].Events.Add((MutationEnd, id));

    public int BeginRead(int key)
    {
        var log = _logs[key];
        var id = 0;
        while (log.Events.Contains((ReadStart, id)))
        {
            id++;
        }

        log.Events.Add((ReadStart, id));
        return id;
    }

    public void DropRead(int key, int readId) => _logs[key].Events.Remove((ReadStart, readId));

    // A call that gave up: its interval ends, but it deleted or wrote nothing that a later read must reflect.
    public void AbortMutation(int key, int id) => _logs[key].Events.Add((Abort, id));

    // The key may have lost its value (evicted, expired): misses are excused until a mutation that starts after this completes.
    public void Disturb(int key) => _logs[key].Events.Add((Disturbance, 0));

    // A failover lost writes: mutations that completed before it no longer constrain later reads.
    public void Lose(int key)
    {
        _logs[key].Events.Add((Void, 0));
        _logs[key].Events.Add((Disturbance, 0));
    }

    // A read of a key ended with this result (null for a miss). Reads never wait for mutations, so the checks are about what they may not return.
    // A hit from a lagging replica may predate a completed mutation. The other read invariants still apply.
    public OracleViolation? EndRead(int key, int readId, string? result, string direction, bool final, bool replicaHit = false)
    {
        var log = _logs[key];
        var start = log.Events.IndexOf((ReadStart, readId));
        log.Events.Remove((ReadStart, readId));
        var lastVoid = log.Events.FindLastIndex(e => e.Kind == Void);
        // A mutation that was already running when writes were lost may have lost its own effect, so only mutations that began after the loss constrain a read.
        var completed = Enumerable.Range(0, log.Values.Count).Where(m => { var e = log.End(m); return log.Start(m) > lastVoid && e > lastVoid && e >= 0 && e < start; }).ToList();
        bool Superseded(int m) => log.End(m) >= 0 && completed.Any(m2 => m2 != m && log.End(m) < log.Start(m2));

        if (result is not null)
        {
            return CheckHit(_keys[key].Name, log, result, final, replicaHit, Superseded);
        }

        return CheckMiss(_keys[key], log, start, direction, final, completed, Superseded);
    }

    private static OracleViolation? CheckHit(string name, KeyLog log, string result, bool final, bool replicaHit, Func<int, bool> superseded)
    {
        var producers = Enumerable.Range(0, log.Values.Count).Where(m => log.Values[m] == result).ToList();
        if (result.StartsWith("!partial", StringComparison.Ordinal))
        {
            return new OracleViolation(OracleViolations.PartialHash, $"{name}: a read returned a partial hash {result}", final);
        }

        if (producers.Count == 0 && !result.StartsWith("merged:", StringComparison.Ordinal))
        {
            return new OracleViolation(OracleViolations.Stale, $"{name}: a read returned '{result}', which no mutation wrote", final);
        }

        return !replicaHit && producers.Count > 0 && producers.All(superseded)
            ? new OracleViolation(OracleViolations.Stale, $"{name}: a read returned '{result}', which a mutation that completed before the read started had replaced", final)
            : null;
    }

    private static OracleViolation? CheckMiss(OracleKey spec, KeyLog log, int start, string direction, bool final, List<int> completed, Func<int, bool> superseded)
    {
        var maximal = completed.Where(m => !superseded(m)).ToList();
        var lastDisturbance = log.Events.FindLastIndex(e => e.Kind == Disturbance);
        var excused = lastDisturbance >= 0 && !Enumerable.Range(0, log.Values.Count).Any(m => log.Values[m] is not null && log.Start(m) > lastDisturbance && log.End(m) >= 0 && log.End(m) < start);
        if (excused || maximal.Count == 0 || maximal.Any(m => log.Values[m] is null))
        {
            return null;
        }

        var overlapping = Enumerable.Range(0, log.Values.Count)
            .Where(m => log.Start(m) >= 0 && !(log.End(m) >= 0 && log.End(m) < start) && !(log.AbortedAt(m) >= 0 && log.AbortedAt(m) < start))
            .ToList();
        var deleting = overlapping.Any(m => log.Values[m] is null);
        var overwriting = overlapping.Count > 0;
        var allowed = deleting || (overwriting && !(spec.NoOrigin && direction == "fwd"));
        var inFlight = overwriting ? "only an overwrite" : "no mutation";
        return allowed ? null : new OracleViolation(OracleViolations.FalseMiss, $"{spec.Name}: a stored key read as a miss with {inFlight} in flight", final);
    }

    private sealed class KeyLog
    {
        public List<string?> Values { get; init; } = [];

        public List<(byte Kind, int Id)> Events { get; init; } = [];

        public int Start(int m) => Events.IndexOf((MutationStart, m));

        public int End(int m) => Events.IndexOf((MutationEnd, m));

        public int AbortedAt(int m) => Events.IndexOf((HistoryOracle.Abort, m));
    }
}
