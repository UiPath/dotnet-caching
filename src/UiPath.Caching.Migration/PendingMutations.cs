namespace UiPath.Caching.Migration;

/// <summary>Keys of one cache whose last write has an unknown outcome: off the local tier until the late commit can no longer land, then evicted and announced again. One timer per key, and a key's retirement and replacement never interleave.</summary>
internal sealed class PendingMutations(RouterCore core, string cache, Func<string, ValueTask> announceRemoval, Action<string> detach)
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private TaskCompletionSource? _empty;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void Begin(string key, TimeSpan window)
    {
        var until = core.Clock.GetUtcNow() + window;
        Entry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.Until = existing.Until > until ? existing.Until : until;
                return;
            }

            entry = new Entry { Until = until };
            _entries[key] = entry;
            core.Tier.BeginPending(new LocalKey(cache, key));
        }

        core.Run(ct => WaitAsync(key, entry, ct));
    }

    /// <summary>Completes when no key is pending.</summary>
    public Task WhenEmptyAsync()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return Task.CompletedTask;
            }

            _empty ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _empty.Task;
        }
    }

    /// <summary>The final eviction and announcement of every key still pending, once and best effort.</summary>
    public async Task FlushAsync()
    {
        string[] keys;
        lock (_gate)
        {
            keys = [.. _entries.Keys];
            foreach (var key in keys)
            {
                _entries[key].Done = true;
                Retire(key);
            }
        }

        foreach (var key in keys)
        {
            await AnnounceAsync(key).ConfigureAwait(false);
        }
    }

    /// <summary>Under the gate: evicts, ends the pending protection and removes the entry, so no replacement can come in between.</summary>
    private void Retire(string key)
    {
        var local = new LocalKey(cache, key);
        core.Tier.Evict(local);
        core.Tier.EndPending(local);
        _entries.Remove(key);
        if (_entries.Count == 0)
        {
            _empty?.TrySetResult();
            _empty = null;
        }
    }

    private async Task WaitAsync(string key, Entry entry, CancellationToken stopping)
    {
        while (true)
        {
            TimeSpan left;
            lock (_gate)
            {
                if (entry.Done)
                {
                    return;
                }

                left = entry.Until - core.Clock.GetUtcNow();
                if (left <= TimeSpan.Zero)
                {
                    entry.Done = true;
                    Retire(key);
                    break;
                }
            }

            try
            {
                await Task.Delay(left, core.Clock, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        await AnnounceAsync(key).ConfigureAwait(false);
    }

    private async Task AnnounceAsync(string key)
    {
        detach(key);
        try
        {
            await announceRemoval(key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The announcement is best effort here; a failed one is already queued for retry by the caller of announceRemoval.
        }
    }

    private sealed class Entry
    {
        public DateTimeOffset Until { get; set; }

        public bool Done { get; set; }
    }
}
