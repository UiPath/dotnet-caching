namespace UiPath.Caching.Tests.PhaseB.Harness;

// One server slot: the master's keys, the before-images of its last write scripts (replica lag and failover read them), no clock of its own.
internal sealed class ShardStore
{
    public const string ControlKey = "mg:ctl";
    private const int History = 2;

    private bool _cloned;

    public Dictionary<string, Entry> Data { get; private init; } = [];

    public List<Dictionary<string, Entry>> Past { get; private init; } = [];

    public ShardStore Clone() => new()
    {
        Data = CloneOf(Data, true),
        Past = [.. Past.Select(p => CloneOf(p, true))],
    };

    public Entry? Get(string key) => Data.GetValueOrDefault(key);

    public bool Exists(string key) => Data.ContainsKey(key);

    public string? GetString(string key)
    {
        var e = Get(key);
        return e is null ? null : e.IsHash ? throw new WrongTypeException() : e.Text;
    }

    public string? HGet(string key, string field)
    {
        var e = Get(key);
        return e is null ? null : !e.IsHash ? throw new WrongTypeException() : e.Fields!.GetValueOrDefault(field);
    }

    public T Scripted<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        finally
        {
            _cloned = false;
        }
    }

    public void Scripted(Action body) => Scripted(() =>
    {
        body();
        return 0;
    });

    public void Put(string key, Entry entry)
    {
        Touch(key);
        Data[key] = entry;
    }

    public bool Remove(string key)
    {
        if (!Data.ContainsKey(key))
        {
            return false;
        }

        Touch(key);
        Data.Remove(key);
        return true;
    }

    public void HSet(string key, string field, string value)
    {
        var e = Get(key);
        if (e is { IsHash: false })
        {
            throw new WrongTypeException();
        }

        Touch(key);
        if (e is null)
        {
            e = Entry.Hash([]);
            Data[key] = e;
        }

        e.Fields![field] = value;
    }

    public bool HDel(string key, string field)
    {
        var e = Get(key);
        if (e is { IsHash: false })
        {
            throw new WrongTypeException();
        }

        if (e is null || !e.Fields!.ContainsKey(field))
        {
            return false;
        }

        Touch(key);
        e.Fields.Remove(field);
        if (e.Fields.Count == 0)
        {
            Data.Remove(key);
        }

        return true;
    }

    // PEXPIREAT: an instant at or before now removes the key.
    public bool ExpireAt(string key, long deadline, long now)
    {
        if (!Data.TryGetValue(key, out var e))
        {
            return false;
        }

        Touch(key);
        if (deadline <= now)
        {
            Data.Remove(key);
        }
        else
        {
            e.ExpireAt = deadline;
        }

        return true;
    }

    public bool Persist(string key)
    {
        if (!Data.TryGetValue(key, out var e) || e.ExpireAt == -1)
        {
            return false;
        }

        Touch(key);
        e.ExpireAt = -1;
        return true;
    }

    public void Expire(long now)
    {
        foreach (var key in Data.Where(d => d.Value.ExpireAt != -1 && d.Value.ExpireAt <= now).Select(d => d.Key).ToList())
        {
            Data.Remove(key);
        }
    }

    // The keys a replica read sees, lagging by that many write scripts.
    public Entry? Replica(string key, int lag, long now)
    {
        var view = lag > 0 && Past.Count > 0 ? Past[Math.Max(0, Past.Count - lag)] : Data;
        return view.TryGetValue(key, out var e) && (e.ExpireAt == -1 || e.ExpireAt > now) ? e : null;
    }

    // A failover: the new master lacks the last write scripts. Control records are not rolled back (the state machine's own fault model covers them).
    public int Failover(int lost)
    {
        var n = Math.Min(lost, Past.Count);
        if (n == 0)
        {
            return 0;
        }

        var restored = CloneOf(Past[Past.Count - n], false);
        foreach (var control in Data.Where(d => IsControl(d.Key)))
        {
            restored[control.Key] = control.Value;
        }

        Data.Clear();
        foreach (var d in restored)
        {
            Data[d.Key] = d.Value;
        }

        Past.RemoveRange(Past.Count - n, n);
        return n;
    }

    public void Write(StateHasher hasher)
    {
        WriteMap(hasher, Data);
        foreach (var past in Past)
        {
            hasher.Add("past");
            WriteMap(hasher, past);
        }
    }

    private static bool IsControl(string key) => key == ControlKey;

    private static Dictionary<string, Entry> CloneOf(Dictionary<string, Entry> source, bool withControl) =>
        source.Where(d => withControl || !IsControl(d.Key)).ToDictionary(d => d.Key, d => d.Value.Clone());

    private static void WriteMap(StateHasher hasher, Dictionary<string, Entry> map)
    {
        foreach (var d in map.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            hasher.Add(d.Key);
            d.Value.Write(hasher);
        }
    }

    private void Touch(string key)
    {
        if (_cloned || IsControl(key))
        {
            return;
        }

        _cloned = true;
        Past.Add(CloneOf(Data, false));
        if (Past.Count > History)
        {
            Past.RemoveAt(0);
        }
    }
}
