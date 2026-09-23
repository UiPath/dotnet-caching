namespace UiPath.Caching.Redis;

internal static class TaskObservation
{
    /// <summary>Observes the failure of a task nothing awaits.</summary>
    public static void Forget(this Task task) =>
        _ = task.ContinueWith(
            static failed => _ = failed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
