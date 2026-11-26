namespace UiPath.Caching.Migration;

/// <summary>A real-time bound on a step that may ignore its cancellation token.</summary>
internal static class BoundedStop
{
    /// <summary>True when the task completed in time; otherwise false, with the task left running and its failure observed.</summary>
    public static async Task<bool> CompletesAsync(Task task, TimeSpan bound, TimeProvider? clock = null)
    {
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(bound, clock ?? TimeProvider.System, timer.Token);
        if (await Task.WhenAny(task, delay).ConfigureAwait(false) == delay)
        {
            _ = task.ContinueWith(done => done.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }

        await timer.CancelAsync().ConfigureAwait(false);
        await task.ConfigureAwait(false);
        return true;
    }
}
