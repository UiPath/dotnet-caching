namespace UiPath.Caching.Migration.Tests.KeyMove;

// A rate that never waits and never pauses: the mover tests that are not about its pace.
internal sealed class NoLimit : IMoveRate
{
    public static NoLimit Instance { get; } = new();

    public ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

    public void Owe(string unit, long bytes)
    {
    }

    public long TakeDebt(string unit) => 0;

    public TimeSpan Reserve(string unit, int keys, long bytes) => TimeSpan.Zero;

    public void Observed(string unit, MoveResult result, TimeSpan elapsed)
    {
    }
}
