namespace UiPath.Platform.Caching.Tests;

internal class TestCacheEntry<T> : ICacheEntry<T>
{
    private readonly bool? _foundOverride;

    public T? Value { get; set; }

    object? ICacheEntry.Value => Value;

    public DateTimeOffset Expiration { get; set; } = DateTimeOffset.MaxValue;

    public IDictionary<string, string?>? Metadata { get; set; }

    /// <summary>
    /// Test-only override: legacy mocks set <c>Value = null</c> to represent a miss, so default falls back
    /// on <c>Value is not null</c>. Set <c>Found = true</c> explicitly to mock the cached-null hit state.
    /// </summary>
    public bool Found
    {
        get => _foundOverride ?? (Expiration > DateTimeOffset.MinValue && Value is not null);
        init => _foundOverride = value;
    }

    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? metadata = null) =>
        _foundOverride.HasValue
            ? new TestCacheEntry<T>
            {
                Value = Value,
                Expiration = expiration ?? DateTimeOffset.MaxValue,
                Metadata = metadata,
                Found = _foundOverride.Value,
            }
            : new TestCacheEntry<T>
            {
                Value = Value,
                Expiration = expiration ?? DateTimeOffset.MaxValue,
                Metadata = metadata,
            };
}
