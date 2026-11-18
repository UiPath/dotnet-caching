namespace UiPath.Caching.Migration;

/// <summary>Per-key state of one cache on one node: a lock that serializes writes to the key, and a generation that every settled mutation, peer eviction and final pending eviction advances. An entry lives only while a mutation or a load holds it.</summary>
internal sealed class KeyGates
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly Dictionary<string, KeyState> _states = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    public KeyState Acquire(string key)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state))
            {
                state = new KeyState();
                _states[key] = state;
            }

            state.Users++;
            return state;
        }
    }

    public void Release(string key, KeyState state)
    {
        lock (_gate)
        {
            if (--state.Users == 0)
            {
                _states.Remove(key);
                state.Dispose();
            }
        }
    }

    /// <summary>Advances the generation of a key someone is holding; nobody holding it has no generation to protect.</summary>
    public void Advance(string key)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(key, out var state))
            {
                state.Advance();
            }
        }
    }
}
