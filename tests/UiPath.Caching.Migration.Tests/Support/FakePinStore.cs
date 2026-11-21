using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>
/// The switch state, the acknowledgement records and the node hash in memory, shared by the nodes of a test, with Redis that can be taken down. A router entry in
/// <see cref="Nodes"/> that carries <c>pin=g</c> and has no record of its own counts as acknowledged, as a peer that published one.
/// </summary>
internal sealed class FakePinStore : IPinStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SwitchRead> _state = [];
    private readonly Dictionary<(string Group, string Field), string> _acks = [];
    private readonly List<string> _log = [];

    public List<NodeEntry> Nodes { get; } = [];

    public bool Down { get; set; }

    public bool WritesDown { get; set; }

    /// <summary>Runs inside a state read, after it began and before it answers, to make something happen during it.</summary>
    public Action? DuringStateRead { get; set; }

    /// <summary>Runs after a switch was asked for and before the store checks the acknowledgements, to make something happen in between.</summary>
    public Func<Task>? BeforeCommit { get; set; }

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public SwitchRead StateOf(string group)
    {
        lock (_gate)
        {
            return _state.GetValueOrDefault(group, SwitchRead.Unswitched);
        }
    }

    public void Set(string group, SwitchRead state)
    {
        lock (_gate)
        {
            _state[group] = state;
        }
    }

    public void Lose(string group)
    {
        lock (_gate)
        {
            _state.Remove(group);
        }
    }

    public void SetAck(string group, string field, string record)
    {
        lock (_gate)
        {
            _acks[(group, field)] = record;
        }
    }

    public string? AckOf(string group, string field)
    {
        lock (_gate)
        {
            return CurrentAcks(group).GetValueOrDefault(field);
        }
    }

    public Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        DuringStateRead?.Invoke();
        lock (_gate)
        {
            _log.Add("read:" + group);
            return Task.FromResult(_state.GetValueOrDefault(group, SwitchRead.Unswitched));
        }
    }

    public Task RestoreSwitchAsync(string group, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        if (WritesDown)
        {
            throw new RedisException("read only");
        }

        lock (_gate)
        {
            _log.Add("restore:" + group);
            _state[group] = SwitchRead.Switched;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeEntry>> ReadNodesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDown();
        lock (_gate)
        {
            _log.Add("nodes");
            return Task.FromResult<IReadOnlyList<NodeEntry>>([.. Nodes]);
        }
    }

    public Task<AckPublish> PublishAckAsync(string group, string field, string incarnation, long generation, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        DuringStateRead?.Invoke();
        lock (_gate)
        {
            _log.Add($"publish:{group}:{generation}");
            var applied = true;
            if (_acks.TryGetValue((group, field), out var current))
            {
                var separator = current.LastIndexOf(':');
                applied = current[..separator] != incarnation || long.Parse(current[(separator + 1)..]) <= generation;
            }

            if (applied)
            {
                _acks[(group, field)] = $"{incarnation}:{generation}";
            }

            return Task.FromResult(new AckPublish(applied, _state.GetValueOrDefault(group, SwitchRead.Unswitched)));
        }
    }

    public Task<IReadOnlyDictionary<string, string>> ReadAcksAsync(string group, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        lock (_gate)
        {
            _log.Add("acks:" + group);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(CurrentAcks(group));
        }
    }

    public async Task<bool> CommitSwitchAsync(string group, IReadOnlyList<KeyValuePair<string, string>> acknowledgements, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        if (WritesDown)
        {
            throw new RedisException("read only");
        }

        if (BeforeCommit is { } before)
        {
            BeforeCommit = null;
            await before().ConfigureAwait(false);
        }

        lock (_gate)
        {
            var current = CurrentAcks(group);
            if (acknowledgements.Count == 0 || acknowledgements.Any(ack => current.GetValueOrDefault(ack.Key) != ack.Value))
            {
                _log.Add("refused:" + group);
                return false;
            }

            _log.Add("commit:" + group);
            _state[group] = SwitchRead.Switched;
            return true;
        }
    }

    private Dictionary<string, string> CurrentAcks(string group)
    {
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (RouterAdvertisement.TryParse(node.Text, out var advertisement) && advertisement.Has(RouterAdvertisement.Pin + group))
            {
                records[node.Field] = "peer:1";
            }
        }

        foreach (var ((recordGroup, field), record) in _acks)
        {
            if (recordGroup == group)
            {
                records[field] = record;
            }
        }

        return records;
    }

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new RedisException("down");
        }
    }
}
