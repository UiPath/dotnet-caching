using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

internal sealed class PlainPathStub : IPathClient<Item>
{
    public int Reads { get; private set; }

    public int Sets { get; private set; }

    public int Removes { get; private set; }

    public int TryAdds { get; private set; }

    public int Refreshes { get; private set; }

    public int Fills { get; private set; }

    public Item? Held { get; set; }

    public TryAddKind NextTryAdd { get; set; } = TryAddKind.Created;

    public DateTimeOffset? Expiration { get; set; }

    public TimeSpan? RemainingLife { get; set; }

    public ILibraryBatch<Item>? PlainBatch { get; set; }

    public TimeSpan? SettleWithin => null;

    public ValueTask<PathRead<Item>> ReadAsync(string key, CancellationToken cancellationToken)
    {
        Reads++;
        return ValueTask.FromResult(PathRead<Item>.Of(ReadOutcome.Absent));
    }

    public ValueTask<WriteResult> SetAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        Sets++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<AddResult> TryCreateAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        ValueTask.FromResult<AddResult>(AddOutcome.Added);

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Removes++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<WriteResult> FillAsync(string key, Item value, DateTimeOffset? deadline, FillBox<Item>? stored, CancellationToken cancellationToken)
    {
        if (Held is { } winner)
        {
            if (stored is not null)
            {
                stored.Lost = true;
                stored.Stored = winner;
            }

            return ValueTask.FromResult<WriteResult>(WriteOutcome.NotSent);
        }

        return SetAsync(key, value, deadline, cancellationToken);
    }

    public ValueTask<TryAddResult<Item>> TryAddAsync(string key, Item? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        TryAdds++;
        return ValueTask.FromResult(NextTryAdd == TryAddKind.Created ? TryAddResult<Item>.Created : TryAddResult<Item>.Exists(null));
    }

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        Refreshes++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<FillResult<Item>> TryFillAsync(string key, Item? value, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        Fills++;
        return ValueTask.FromResult(new FillResult<Item>(FillOutcome.Added, null, deadline));
    }

    public ValueTask<TimeSpan?> TimeToLiveAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RemainingLife);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Expiration);

    public ValueTask<SwapResult<Item>> SetIfChangedAsync(string key, Item? value, IEqualityComparer<Item> comparer, DateTimeOffset? deadline, CancellationToken cancellationToken)
    {
        var existing = Held;
        Held = value;
        return ValueTask.FromResult(new SwapResult<Item>(new ChangeResult<Item>(ChangeOutcome.Replaced, existing, deadline), WriteOutcome.Committed));
    }
}
