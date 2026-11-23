namespace UiPath.Caching;

/// <summary>What an adoption read saw, for the store that follows the generator to compare against; set once, as a whole, so a read that answers late cannot leave half of it.</summary>
internal sealed class StampBox
{
    private volatile Seen? _seen;

    /// <summary>The read answered; <paramref name="stamp"/> is null for an absent key.</summary>
    public bool TryGet(out object? stamp)
    {
        var seen = _seen;
        stamp = seen?.Stamp;
        return seen is not null;
    }

    public void Record(object? stamp) => _seen = new Seen(stamp);

    private sealed record Seen(object? Stamp);
}