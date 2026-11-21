using System.Net;

namespace UiPath.Caching.PhaseB;

/// <summary>The dedicated connections one transition opened, one per master; a failed barrier replaces its connection.</summary>
internal sealed class MasterChannels(IStateTopology topology) : IAsyncDisposable
{
    private readonly Dictionary<EndPoint, IMasterChannel> _open = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IMasterChannel> GetAsync(EndPoint master, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_open.TryGetValue(master, out var channel))
            {
                channel = await topology.OpenAsync(master, cancellationToken).ConfigureAwait(false);
                _open[master] = channel;
            }

            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAsync(EndPoint master, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_open.Remove(master, out var old))
            {
                try
                {
                    await old.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The old connection is being replaced because it failed.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _open.Values)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _open.Clear();
        _gate.Dispose();
    }
}
