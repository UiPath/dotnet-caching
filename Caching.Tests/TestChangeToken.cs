namespace UiPath.Platform.Caching.Tests;

internal class TestChangeToken : ICacheChangeToken, IDisposable
{
    public bool Disposed { get; set; }

    public bool HasChanged { get; set; }

    public bool ActiveChangeCallbacks { get; set; }

    public List<(Action<object?> callback, object? state)> Callbacks { get; } = new();

    public bool MetadataHasChanged { get; set; }

    public DateTimeOffset? Expiration { get; set; }

    public IDictionary<string, string?>? Metadata { get; set; }

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
