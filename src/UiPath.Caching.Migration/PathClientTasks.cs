namespace UiPath.Caching.Migration;

internal static class PathClientTasks
{
    /// <summary>The sentinel for a key with no retry; an instance of its own, so no real task, even a completed one, is taken for it.</summary>
    public static Task NoPendingRetry { get; } = Task.FromResult(0);
}
