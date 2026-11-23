using System.Net;

namespace UiPath.Caching.KeyMove;

/// <summary>Opens a dedicated connection per master without delaying the other masters.</summary>
internal sealed class MasterChannels(IStateTopology topology) : IAsyncDisposable
{
    private readonly Dictionary<EndPoint, Task<IMasterChannel>> _open = [];
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    public async Task<IMasterChannel> GetAsync(EndPoint master, CancellationToken cancellationToken)
    {
        Task<IMasterChannel> opening;
        lock (_lock)
        {
            if (_open.TryGetValue(master, out var current))
            {
                opening = current;
            }
            else
            {
                // Shared by every caller that asks while it opens, so it does not take one caller's cancellation.
                opening = topology.OpenAsync(master, CancellationToken.None);
                _open[master] = opening;
            }
        }

        try
        {
            return await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (opening.IsFaulted && ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                if (_open.TryGetValue(master, out var current) && ReferenceEquals(current, opening))
                {
                    _open.Remove(master);
                }
            }

            throw;
        }
    }

    public async Task ReplaceAsync(EndPoint master, CancellationToken cancellationToken)
    {
        Task<IMasterChannel>? old;
        lock (_lock)
        {
            _open.Remove(master, out old);
        }

        if (old is null)
        {
            return;
        }

        try
        {
            await (await old.WaitAsync(cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The old connection is being replaced because it failed, or never opened.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<IMasterChannel>[] all;
        lock (_lock)
        {
            all = [.. _open.Values];
            _open.Clear();
        }

        foreach (var opening in all)
        {
            try
            {
                await (await opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A connection that never opened has nothing to close.
            }
        }
    }
}
