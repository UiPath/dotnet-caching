using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

internal sealed class HashPlainStub : IHashPathClient<Item>
{
    public int Reads { get; private set; }

    public int Sets { get; private set; }

    public int Removes { get; private set; }

    public int FieldRemoves { get; private set; }

    public int Refreshes { get; private set; }

    public int Fills { get; private set; }

    public TimeSpan? SettleWithin => null;

    public ValueTask<HashRead<Item>> ReadAsync(string key, string[]? fields, CancellationToken cancellationToken)
    {
        Reads++;
        return ValueTask.FromResult(HashRead<Item>.Of(ReadOutcome.Absent));
    }

    public ValueTask<bool?> ContainsAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<bool?>(null);

    public ValueTask<DateTimeOffset?> ExpireTimeAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<DateTimeOffset?>(null);

    public ValueTask<WriteResult> SetAsync(string key, IDictionary<string, Item?> fields, HashWriteOptions options, CancellationToken cancellationToken)
    {
        Sets++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<WriteResult> RemoveAsync(string key, CancellationToken cancellationToken)
    {
        Removes++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<WriteResult> RemoveFieldsAsync(string key, string[] fields, CancellationToken cancellationToken)
    {
        FieldRemoves++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, CancellationToken cancellationToken)
    {
        Refreshes++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<WriteResult> RefreshAsync(string key, CacheExpiry expiry, IDictionary<string, string?>? metadata, CancellationToken cancellationToken)
    {
        Refreshes++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }

    public ValueTask<WriteResult> FillAsync(string key, IDictionary<string, Item?> fields, HashWriteOptions options, HashFillBox<Item>? stored, CancellationToken cancellationToken)
    {
        Fills++;
        return ValueTask.FromResult<WriteResult>(WriteOutcome.Committed);
    }
}
