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

    /// <summary>Hands a client task to a <see cref="ValueTask{TResult}"/> callback without an async state machine around it.</summary>
    public static ValueTask<TResult> AsValueTask<TResult>(this Task<TResult> task) => new(task);
}
