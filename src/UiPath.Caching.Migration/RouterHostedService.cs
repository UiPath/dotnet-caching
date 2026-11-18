using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Migration;

internal sealed class RouterHostedService(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<IRouter>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        services.GetRequiredService<IRouter>() is Router router ? router.StopAsync(cancellationToken) : Task.CompletedTask;
}
