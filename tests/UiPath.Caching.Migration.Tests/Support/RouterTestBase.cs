namespace UiPath.Caching.Migration.Tests.Support;

public abstract class RouterTestBase : IAsyncDisposable
{
    private readonly List<TestNode> _nodes = [];

    internal ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    internal FakeBus Bus { get; } = new();

    internal Dictionary<string, Item> Store { get; } = [];

    internal string CacheName { get; } = $"cache{Guid.NewGuid():N}"[..14];

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    internal TestNode NewNode(
        Func<string, bool>? useLibrary = null,
        TimeSpan? localCap = null,
        TimeSpan? pendingWindow = null,
        string? cacheName = null,
        TimeSpan? defaultExpiry = null,
        TestNode? sharing = null,
        IPathClient<Item>? library = null)
    {
        var node = TestNode.Create(Bus, Clock, Store, cacheName ?? CacheName, useLibrary, localCap, pendingWindow, defaultExpiry: defaultExpiry, sharedCore: sharing?.Core, library: library);
        _nodes.Add(node);
        return node;
    }

    /// <summary>Moves the clock in steps until the node has no background work left, waiting for each timer to be armed first.</summary>
    internal async Task RunBackgroundAsync(TestNode node, TimeSpan step)
    {
        for (var rounds = 0; rounds < 200; rounds++)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Clock.HasTimers && DateTime.UtcNow < deadline)
            {
                if (node.Core.IsIdle)
                {
                    return;
                }

                await Task.Delay(5, Ct);
            }

            if (!Clock.HasTimers)
            {
                return;
            }

            Clock.Advance(step);
            await Task.Delay(20, Ct);
        }

        throw new TimeoutException("background work never finished");
    }
}
