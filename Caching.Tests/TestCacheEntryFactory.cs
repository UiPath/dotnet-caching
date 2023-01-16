namespace UiPath.Platform.Caching.Tests;

internal class TestCacheEntryFactory : ICacheEntryFactory
{
    public ICacheEntry<T> Create<T>(T Value, DateTimeOffset expiration, IDictionary<string, string?>? properties = default) =>
        new TestCacheEntry<T> { Expiration = expiration, Value = Value, ExtendedProperties = properties };
}

internal class TestCacheEntry<T> : ICacheEntry<T>
{
    public T? Value { get; set; }

    public DateTimeOffset Expiration { get; set; } = DateTimeOffset.MaxValue;

    public IDictionary<string, string?>? ExtendedProperties { get; set; }

    public ICacheEntry NewEntry(DateTimeOffset? expiration = null, IDictionary<string, string?>? extendedProperties = null) =>
        new TestCacheEntry<T>
        {
            Value = Value,
            Expiration = expiration ?? DateTimeOffset.MaxValue,
            ExtendedProperties = extendedProperties
        };
}

internal class TestChangeToken : IExtendedPropertiesChangeToken, IDisposable
{
    public bool Disposed { get; set; }

    public bool HasChanged { get; set; }

    public bool ActiveChangeCallbacks { get; set; }

    public List<(Action<object?> callback, object? state)> Callbacks { get; } = new();

    public bool ExtendedPropertiesHasChanged { get; set; }

    public void Dispose()
    {
        Disposed = true;
    }

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        Callbacks.Add((callback, state));
        return this;
    }

    public void InvokeCallbacks() =>
        Callbacks.ToList().ForEach(cb => cb.callback.Invoke(cb.state));

    public async Task AssertIsDisposed()
    {
        var tries = 0;
        while (!Disposed && tries++ < 10)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15));
        }

        Disposed.Should().BeTrue();
    }
}
