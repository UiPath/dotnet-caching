using Polly;
using UiPath.Platform.Caching.Polly;

namespace UiPath.Platform.Caching.Tests;

public class PolicyWrapperTests
{
    [Fact]
    public async Task TaskWithResult_ShouldCompleteSuccessfully()
    {
        var cts = new CancellationTokenSource();
        var sut = new PolicyWrapper(Policy.NoOpAsync());
        var actual = await sut.ExecuteAsync(AsyncMethodWithoutCancellation, cts.Token);
        actual.Should().Be(67);
    }

    [Fact]
    public async Task TaskWithResult_ShouldCancelSuccessfully()
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        var sut = new PolicyWrapper(Policy.NoOpAsync());
        Func<Task> act = async () => await sut.ExecuteAsync(AsyncMethodWithoutCancellation, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Task_ShouldCompleteSuccessfully()
    {
        var cts = new CancellationTokenSource();
        var sut = new PolicyWrapper(Policy.NoOpAsync());
        Func<Task> act = async () => await sut.ExecuteAsync(() => Task.Delay(2000), cts.Token);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Task_ShouldCancelSuccessfully()
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        var sut = new PolicyWrapper(Policy.NoOpAsync());
        Func<Task> act = async () => await sut.ExecuteAsync(() => Task.Delay(2000), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<int> AsyncMethodWithoutCancellation()
    {
        await Task.Delay(1000);
        return 67;
    }
}
