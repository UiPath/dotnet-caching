using UiPath.Caching.Broadcast;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class FlightFenceTests : RouterTestBase
{
    [Theory]
    [InlineData("set")]
    [InlineData("remove")]
    [InlineData("clear")]
    public async Task A_caller_that_starts_after_a_mutation_settled_does_not_join_the_older_load(string mutation)
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> OldLoad(CancellationToken token)
        {
            entered.TrySetResult();
            await gate.Task;
            return new Item("old");
        }

        var leader = node.Cache.GetOrAddAsync("k", OldLoad, default, Ct).AsTask();
        await entered.Task;
        switch (mutation)
        {
            case "set":
                await node.Cache.SetAsync("k", new Item("new"), default, Ct);
                break;
            case "remove":
                await node.Cache.RemoveAsync("k", Ct);
                break;
            default:
                await node.Cache.ClearAsync(Ct);
                break;
        }

        var after = await node.Cache.GetOrAddAsync("k", _ => Task.FromResult<Item?>(new Item("fresh")), default, Ct);

        after.Should().Be(mutation == "set" ? new Item("new") : new Item("fresh"), "the caller read after the mutation and did not wait for the older load");
        gate.SetResult();
        await leader;
    }

    [Theory]
    [InlineData("set")]
    [InlineData("remove")]
    [InlineData("clear")]
    [InlineData("peer")]
    public async Task A_leader_whose_key_was_mutated_while_its_factory_ran_does_not_write(string mutation)
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var peer = NewNode();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Item?> OldLoad(CancellationToken token)
        {
            entered.TrySetResult();
            await gate.Task;
            return new Item("old");
        }

        var leader = node.Cache.GetOrAddAsync("k", OldLoad, default, Ct).AsTask();
        await entered.Task;
        var writesBefore = node.LibraryBackend.Writes;
        switch (mutation)
        {
            case "set":
                await node.Cache.SetAsync("k", new Item("new"), default, Ct);
                writesBefore++;
                break;
            case "remove":
                await node.Cache.RemoveAsync("k", Ct);
                break;
            case "clear":
                await node.Cache.ClearAsync(Ct);
                break;
            default:
                await peer.Cache.SetAsync("k", new Item("new"), default, Ct);
                break;
        }

        gate.SetResult();
        (await leader).Should().Be(new Item("old"), "the leader still answers its own caller");

        node.LibraryBackend.Writes.Should().Be(writesBefore, "its stale value is not written over the newer state");
        if (mutation is "set" or "peer")
        {
            Store["k"].Should().Be(new Item("new"));
        }
        else
        {
            Store.Should().NotContainKey("k");
        }
    }

    [Fact]
    public async Task A_follower_does_not_retry_when_the_factory_itself_threw_a_cancellation()
    {
        var node = NewNode(localCap: TimeSpan.Zero);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        async Task<Item?> Cancelling(CancellationToken token)
        {
            runs++;
            entered.TrySetResult();
            await gate.Task;
            throw new OperationCanceledException("the origin cancelled it");
        }

        var leader = node.Cache.GetOrAddAsync("k", Cancelling, default, Ct).AsTask();
        await entered.Task;
        var follower = node.Cache.GetOrAddAsync("k", Cancelling, default, Ct).AsTask();
        gate.SetResult();

        await leader.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        await follower.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();
        runs.Should().Be(1);
    }

    [Fact]
    public void A_peer_observer_clears_on_error_completion_a_topic_invalidation_and_a_keyless_event()
    {
        var calls = new List<string>();
        var observer = new PeerObserver(new Uri("urn:self"), key => calls.Add($"evict:{key}"), () => calls.Add("clear"));

        observer.OnError(new IOException("topic failed"));
        observer.OnCompleted();
        observer.OnNext(new StubEvent(KnownEventTypes.CacheRemoved, "k"));
        observer.OnNext(new StubEvent(KnownEventTypes.TopicInvalidated, "*"));
        observer.OnNext(new StubEvent(KnownEventTypes.CacheRemoved, null));
        observer.OnNext(new StubEvent(KnownEventTypes.CacheRemoved, string.Empty));

        calls.Should().Equal("clear", "clear", "evict:k", "clear", "clear", "clear");
    }

    private sealed class StubEvent(string type, string? key) : ICacheEvent
    {
        public CacheEventData? Data => null;

        public string? Id => "id";

        public string? TransportId => null;

        public string? Type => type;

        public Uri? Source => new("urn:elsewhere");

        public string? Key => key;

        public bool IsValid() => true;

        public void AttachTransportId(string? transportId)
        {
        }
    }
}
