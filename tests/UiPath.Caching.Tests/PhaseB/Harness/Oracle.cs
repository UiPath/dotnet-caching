using System.Collections.Immutable;

namespace UiPath.Caching.Tests.PhaseB.Harness;

// The real-time history of mutations and reads of each key, and the checks of T-B1 over it.
// Only the order of events matters, so the history is a list of events and two worlds with the same order are the same state.
internal sealed class Oracle
{
    private const byte MutationStart = 0;
    private const byte MutationEnd = 1;
    private const byte ReadStart = 2;
    private const byte Void = 4;

    private readonly ImmutableArray<KeySpec> _keys;
    private readonly KeyLog[] _logs;
    private readonly Dictionary<string, long> _deadlines = [];

    public Oracle(ImmutableArray<KeySpec> keys)
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
                    _deadlines[initial] = keys[i].Ttl;
                }
            }

            if (keys[i].InitialL is null && keys[i].InitialN is null)
            {
                EndMutation(i, BeginMutation(i, null));
            }
        }
    }

    private Oracle(ImmutableArray<KeySpec> keys, KeyLog[] logs, Dictionary<string, long> deadlines)
    {
        _keys = keys;
        _logs = logs;
        _deadlines = deadlines;
    }

    public Oracle Clone() => new(_keys, [.. _logs.Select(l => l.Clone())], new Dictionary<string, long>(_deadlines));

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

    public void Disturb(int key) => _logs[key].Disturbed = true;

    // A failover lost writes: mutations that completed before it no longer constrain later reads.
    public void Lose(int key)
    {
        _logs[key].Disturbed = true;
        _logs[key].Events.Add((Void, 0));
    }

    public void NoteDeadline(string version, long expireAt) => _deadlines[version] = expireAt;

    // A read of a key ended with this result (null for a miss). Reads never wait for mutations, so the checks are about what they may not return.
    // A hit from a lagging replica may be older than a completed mutation: that is the replica-lag exception of I3, so only its other checks apply.
    public Violation? EndRead(int key, int readId, string? result, string direction, bool final, bool replicaHit = false)
    {
        var log = _logs[key];
        var spec = _keys[key];
        var start = log.Events.IndexOf((ReadStart, readId));
        log.Events.Remove((ReadStart, readId));
        var lastVoid = log.Events.FindLastIndex(e => e.Kind == Void);
        // A mutation that was already running when writes were lost may have lost its own effect, so only mutations that began after the loss constrain a read.
        var completed = Enumerable.Range(0, log.Values.Count).Where(m => { var e = log.End(m); return log.Start(m) > lastVoid && e > lastVoid && e >= 0 && e < start; }).ToList();
        bool Superseded(int m) => log.End(m) >= 0 && completed.Any(m2 => m2 != m && log.End(m) < log.Start(m2));

        if (result is not null)
        {
            var producers = Enumerable.Range(0, log.Values.Count).Where(m => log.Values[m] == result).ToList();
            if (result.StartsWith("!partial", StringComparison.Ordinal))
            {
                return new Violation(ViolationKinds.PartialHash, $"{spec.Name}: a read returned a partial hash {result}", final);
            }

            return !replicaHit && producers.Count > 0 && producers.All(Superseded)
                ? new Violation(ViolationKinds.Stale, $"{spec.Name}: a read returned '{result}', which a mutation that completed before the read started had replaced", final)
                : null;
        }

        var maximal = completed.Where(m => !Superseded(m)).ToList();
        if (log.Disturbed || maximal.Count == 0 || maximal.Any(m => log.Values[m] is null))
        {
            return null;
        }

        var overlapping = Enumerable.Range(0, log.Values.Count)
            .Where(m => log.Start(m) >= 0 && !(log.End(m) >= 0 && log.End(m) < start))
            .ToList();
        var deleting = overlapping.Any(m => log.Values[m] is null);
        var overwriting = overlapping.Count > 0;
        var allowed = deleting || (overwriting && !(spec.NoOrigin && direction == "fwd"));
        return allowed
            ? null
            : new Violation(ViolationKinds.FalseMiss, $"{spec.Name}: a stored key read as a miss with {(overwriting ? "only an overwrite" : "no mutation")} in flight", final);
    }

    // State invariants: no partial hash on either side, and no copy that outlives the deadline of its value.
    public Violation? CheckEntries(World w)
    {
        for (var i = 0; i < _keys.Length; i++)
        {
            foreach (var (entry, side) in new[] { (w.L.Get(_keys[i].L), "L"), (w.N.Get(_keys[i].N), "N") })
            {
                if (entry is null)
                {
                    continue;
                }

                var version = entry.Version();
                if (version.StartsWith("!partial", StringComparison.Ordinal))
                {
                    return new Violation(ViolationKinds.PartialHash, $"{_keys[i].Name}: {side} holds a partial hash {version}");
                }

                if (_deadlines.TryGetValue(version, out var deadline) && deadline != -1 && (entry.ExpireAt == -1 || entry.ExpireAt > deadline))
                {
                    return new Violation(ViolationKinds.Outlives, $"{_keys[i].Name}: the {side} copy of '{version}' expires at {entry.ExpireAt}, past its source deadline {deadline}");
                }
            }
        }

        return null;
    }

    public void Write(StateHasher hasher)
    {
        foreach (var log in _logs)
        {
            hasher.Add(log.Disturbed ? "d" : "-");
            foreach (var v in log.Values)
            {
                hasher.Add(v);
            }

            foreach (var (kind, id) in log.Events)
            {
                hasher.Add(kind * 1000 + id);
            }
        }

        foreach (var d in _deadlines.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            hasher.Add(d.Key);
            hasher.Add(d.Value);
        }
    }

    private sealed class KeyLog
    {
        public List<string?> Values { get; init; } = [];

        public List<(byte Kind, int Id)> Events { get; init; } = [];

        public bool Disturbed { get; set; }

        public KeyLog Clone() => new() { Values = [.. Values], Events = [.. Events], Disturbed = Disturbed };

        public int Start(int m) => Events.IndexOf((MutationStart, m));

        public int End(int m) => Events.IndexOf((MutationEnd, m));
    }
}
