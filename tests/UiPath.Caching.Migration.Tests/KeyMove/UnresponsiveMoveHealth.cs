using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

/// <summary>A health provider that stays pending even after cancellation until the test restores it.</summary>
internal sealed class UnresponsiveMoveHealth : IKeyMoveHealthSource
{
    private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<KeyMoveHealth> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Called => _called.Task;

    public void Recover() => _answer.TrySetResult(new KeyMoveHealth(KeyMoveHealthState.Green, "healthy"));

    public ValueTask<KeyMoveHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        _called.TrySetResult();
        return new ValueTask<KeyMoveHealth>(_answer.Task);
    }
}
