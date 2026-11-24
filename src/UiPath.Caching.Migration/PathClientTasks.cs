namespace UiPath.Caching.Migration;

internal static class PathClientTasks
{
    /// <summary>The sentinel for a key with no retry; a task nothing else can return: <c>Task.FromResult(0)</c> is a cached instance shared by the whole process, so it could not stand for it.</summary>
    public static Task NoPendingRetry { get; } = Completed();

    private static Task Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source.Task;
    }
}
