namespace UiPath.Caching.Migration;

/// <summary>A constant rate in keys per second for each unit; zero or less means unlimited.</summary>
internal sealed class FixedMoveRate(int keysPerSecond, TimeProvider time) : IMoveRate
{
    /// <summary>The safe rate of a unit until the adaptive one exists: the start rate of the design.</summary>
    public const int DefaultKeysPerSecond = 100;

    public ValueTask WaitAsync(int keys, CancellationToken cancellationToken) =>
        keysPerSecond <= 0 || keys <= 0
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(TimeSpan.FromSeconds((double)keys / keysPerSecond), time, cancellationToken));
}
