using Microsoft.Extensions.Hosting;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>A hosted service of the library stack that remembers what the router did with it.</summary>
internal sealed class FakeHostedService : IHostedService
{
    public int Started { get; private set; }

    public int Stopped { get; private set; }

    public TaskCompletionSource StopCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stopped++;
        StopCalled.TrySetResult();
        return Task.CompletedTask;
    }
}

/// <summary>A hosted service whose stop ignores its token and never returns.</summary>
internal sealed class HangingHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => new TaskCompletionSource().Task;
}

/// <summary>A hosted service that cannot start.</summary>
internal sealed class FailingHostedService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("cannot start");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
