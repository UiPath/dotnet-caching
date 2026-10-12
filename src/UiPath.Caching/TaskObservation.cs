namespace UiPath.Caching;

internal static class TaskObservation
{
    /// <summary>Observes the failure of a task nothing awaits.</summary>
    public static void Forget(this Task task) =>
        _ = task.ContinueWith(
            static failed => _ = failed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Observes the failure of a task nothing awaits that has settled, which costs nothing; one still running is forgotten.</summary>
    public static void Observe(this Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
        }
        else
        {
            task.Forget();
        }
    }

    /// <summary>Hands a client task to a <see cref="ValueTask{TResult}"/> callback without an async state machine around it.</summary>
    public static ValueTask<TResult> AsValueTask<TResult>(this Task<TResult> task) => new(task);
}
