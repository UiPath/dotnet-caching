using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Migration;

/// <summary>Runs one mover host per key move group while the library is up. Started after the key move node, so it stops before it.</summary>
internal sealed class MoverHostedService(IReadOnlyList<MoverHost> hosts) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var host in hosts)
        {
            host.Start();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.WhenAll(hosts.Select(host => host.StopAsync()));
}
