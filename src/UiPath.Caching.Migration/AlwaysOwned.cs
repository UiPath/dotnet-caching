namespace UiPath.Caching.Migration;

/// <summary>The ownership of a promotion that has no lease: a read-through.</summary>
internal sealed class AlwaysOwned : IMoveOwnership
{
    public static AlwaysOwned Instance { get; } = new();

    public bool StillHeld => true;

    public ValueTask<bool> HoldsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
