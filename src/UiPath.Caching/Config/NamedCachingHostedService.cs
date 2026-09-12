using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Config;

/// <summary>Runs the stack's hosted services with the host, which starts only what is registered with it.</summary>
internal sealed class NamedCachingHostedService(string name, IServiceProvider root) : IHostedService
{
    private readonly List<IHostedService> _started = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var service in NamedCachingContainer.Of(root, name).Provider.GetServices<IHostedService>())
        {
            await service.StartAsync(cancellationToken).ConfigureAwait(false);
            _started.Add(service);
        }
    }

    /// <summary>Stops every service even when one fails, as the generic host does, and reports the failures together.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        for (var i = _started.Count - 1; i >= 0; i--)
        {
            try
            {
                await _started[i].StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException($"One or more hosted services of named caching stack '{name}' failed to stop.", failures);
        }
    }
}
