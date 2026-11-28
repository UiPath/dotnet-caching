namespace UiPath.Caching.Tests;

internal sealed class TestChangeToken : ICacheChangeToken, IDisposable
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Disposed { get; set; }

    public bool HasChanged { get; set; }

    public bool ActiveChangeCallbacks { get; set; }

    public List<(Action<object?> callback, object? state)> Callbacks { get; } = [];

    public bool MetadataHasChanged { get; set; }

    public DateTimeOffset? Expiration { get; set; }

    public IDictionary<string, string?>? Metadata { get; set; }

    public string? TransportId {get; set;}

    public void Dispose()
    {
        Disposed = true;
        _disposed.TrySetResult();
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
        await _disposed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Disposed.Should().BeTrue();
    }
}
