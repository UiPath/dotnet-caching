using UiPath.Caching.KeyMove;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Starts the key move node once the library stack is up and stops it before the stack goes down.</summary>
internal sealed class KeyMoveHostedService(KeyMoveNode node, IRedisConnector connector, string connectorPrefix, IAdvertSource? adverts, string? scope) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => node.StartAsync(connector, connectorPrefix, adverts, scope, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => node.StopAsync();
}
