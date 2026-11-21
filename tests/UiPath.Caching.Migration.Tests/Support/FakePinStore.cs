using StackExchange.Redis;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>The switch state and the node hash in memory, shared by the nodes of a test, with Redis that can be taken down.</summary>
internal sealed class FakePinStore : IPinStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SwitchRead> _state = [];
    private readonly List<string> _log = [];

    public List<NodeEntry> Nodes { get; } = [];

    public bool Down { get; set; }

    public bool WritesDown { get; set; }

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

    public Task<SwitchRead> ReadStateAsync(string group, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        lock (_gate)
        {
            _log.Add("read:" + group);
            return Task.FromResult(_state.GetValueOrDefault(group, SwitchRead.Unswitched));
        }
    }

    public Task MarkSwitchedAsync(string group, CancellationToken cancellationToken)
    {
        ThrowIfDown();
        if (WritesDown)
        {
            throw new RedisException("read only");
        }

        lock (_gate)
        {
            _log.Add("mark:" + group);
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

    private void ThrowIfDown()
    {
        if (Down)
        {
            throw new RedisException("down");
        }
    }
}