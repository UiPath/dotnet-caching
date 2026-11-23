using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

internal sealed class PlainPathStub : IPathClient<Item>
{
    public int Reads { get; private set; }

    public int Sets { get; private set; }

    public int Removes { get; private set; }

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

    public ValueTask<AddResult> TryAddAsync(string key, Item value, DateTimeOffset? deadline, CancellationToken cancellationToken) =>
        ValueTask.FromResult<AddResult>(AddOutcome.Added);

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Removes++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }
}
