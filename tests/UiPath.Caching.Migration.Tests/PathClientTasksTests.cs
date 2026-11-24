namespace UiPath.Caching.Migration.Tests;

public sealed class PathClientTasksTests
{
    [Fact]
    public void The_no_retry_sentinel_is_completed_and_one_instance()
    {
        PathClientTasks.NoPendingRetry.IsCompletedSuccessfully.Should().BeTrue();
        PathClientTasks.NoPendingRetry.Should().BeSameAs(PathClientTasks.NoPendingRetry);
    }

    // The runtime hands out cached tasks for completed results; a real settlement that is one of them must never read as no retry.
    [Fact]
    public void No_cached_completed_task_is_taken_for_the_no_retry_sentinel()
    {
        Task[] cached =
        [
            Task.CompletedTask,
            Task.FromResult(true),
            Task.FromResult(false),
            Task.FromResult<object?>(null),
            .. Enumerable.Range(-1, 11).Select(Task.FromResult),
        ];

        cached.Should().NotContain(task => ReferenceEquals(task, PathClientTasks.NoPendingRetry));
    }
}
