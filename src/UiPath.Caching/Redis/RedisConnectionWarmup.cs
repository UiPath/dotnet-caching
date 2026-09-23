using Microsoft.Extensions.Hosting;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

internal sealed class RedisConnectionWarmup(IRedisConnector connector, ICachingTelemetryProvider telemetryProvider) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private int _stopped;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Task.Run(() => WarmUpAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token).Forget();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();
    }

    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await connector.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            telemetryProvider.TryTrackException(ex);
        }
    }
}
