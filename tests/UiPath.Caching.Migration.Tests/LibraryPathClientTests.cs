using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class LibraryPathClientTests : RouterTestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(7);

    private readonly FakeMigrationCache _store = new();
    private readonly FakeConnection _connection = new();

    [Fact]
    public async Task A_read_that_fails_is_unavailable_and_never_absent()
    {
        _store.Read = (_, _) => throw new TimeoutException("redis timeout");

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
    }

    [Fact]
    public async Task A_read_while_disconnected_is_unavailable_without_asking_the_library()
    {
        _connection.IsConnected = false;

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_entry_is_absent_and_the_key_is_the_mapped_text_verbatim()
    {
        var read = await Client(key => $" N:{key} ").ReadAsync("K", Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        _store.Keys.Single().Name.Should().Be(" N:K ");
    }

    [Fact]
    public async Task An_entry_the_cache_could_not_read_is_unreadable_and_not_absent()
    {
        _store.Read = (_, _) => Task.FromResult<object?>(SnapshotOutcome.Unreadable);

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unreadable);
    }

    [Fact]
    public async Task A_failed_snapshot_is_unavailable_and_never_absent()
    {
        _store.Read = (_, _) => Task.FromResult<object?>(SnapshotOutcome.Failed);

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Unavailable);
    }

    [Fact]
    public async Task An_absent_snapshot_is_absent()
    {
        _store.Read = (_, _) => Task.FromResult<object?>(SnapshotOutcome.Absent);

        var read = await Client().ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
    }

    [Fact]
    public async Task A_found_entry_carries_the_ttl_of_the_same_snapshot()
    {
        var store = new FakeMigrationCache(Clock) { Read = (_, _) => Task.FromResult<object?>(new Item("v")) };
        var client = new LibraryPathClient<Item>(store, _connection, key => key, Settle);
        Clock.Advance(TimeSpan.FromSeconds(10));

        var read = await client.ReadAsync("k", Ct);

        read.Kind.Should().Be(ReadOutcome.Found);
        read.Value.Should().Be(new Item("v"));
        read.Expiration.Should().Be(Clock.GetUtcNow().AddMinutes(1));
    }

    [Theory]
    [InlineData(StoreOutcome.Unknown)]
    [InlineData(StoreOutcome.Failed)]
    public async Task A_write_that_may_have_been_sent_is_unknown_and_carries_the_settle_deadline(StoreOutcome stored)
    {
        _store.Write = (_, _) => Task.FromResult(stored);

        var result = await Client().SetAsync("k", new Item("v"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        result.SettleWithin.Should().Be(Settle);
    }

    [Fact]
    public async Task A_committed_write_and_delete_are_committed()
    {
        (await Client().SetAsync("k", new Item("v"), null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await Client().RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Committed);
    }

    [Fact]
    public async Task A_write_the_cache_refused_before_sending_is_not_sent()
    {
        _store.Write = (_, _) => Task.FromResult(StoreOutcome.NotSent);

        (await Client().SetAsync("k", new Item("v"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await Client().RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public async Task A_write_that_throws_is_unknown()
    {
        _store.Write = (_, _) => throw new TimeoutException("redis timeout");

        var result = await Client().SetAsync("k", new Item("v"), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        result.SettleWithin.Should().Be(Settle);
    }
    [Fact]
    public async Task A_write_while_disconnected_is_not_sent()
    {
        _connection.IsConnected = false;

        (await Client().SetAsync("k", new Item("v"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await Client().RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_token_cancelled_before_the_send_is_not_sent()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Client().SetAsync("k", new Item("v"), null, cts.Token);

        result.Outcome.Should().Be(WriteOutcome.NotSent);
        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_map_that_throws_is_not_sent()
    {
        var client = Client(_ => throw new InvalidOperationException("no such tenant"));

        (await client.SetAsync("k", new Item("v"), null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
    }

    [Fact]
    public async Task A_write_takes_the_policy_asked_at_that_write_and_none_when_no_policy_is_given()
    {
        var policies = new Queue<CachePolicy>([WriteFormats.Envelope, WriteFormats.MessagePackPolicy]);
        var picking = new LibraryPathClient<Item>(_store, _connection, key => key, () => Settle, () => new WriteLease(policies.Dequeue(), null));

        await Client().SetAsync("k", new Item("v"), null, Ct);
        await picking.SetAsync("k", new Item("v"), null, Ct);
        await picking.SetAsync("k", new Item("v"), null, Ct);
        await picking.RemoveAsync("k", Ct);

        _store.WritePolicies.Should().Equal(null, WriteFormats.Envelope, WriteFormats.MessagePackPolicy);
    }

    [Fact]
    public async Task A_create_takes_the_policy_asked_at_that_create_and_none_when_no_policy_is_given()
    {
        var policies = new Queue<CachePolicy>([WriteFormats.Envelope, WriteFormats.MessagePackPolicy]);
        var picking = new LibraryPathClient<Item>(_store, _connection, key => key, () => Settle, () => new WriteLease(policies.Dequeue(), null));

        await Client().TryCreateAsync("k", new Item("v"), null, Ct);
        await picking.TryCreateAsync("k", new Item("v"), null, Ct);
        await picking.TryCreateAsync("k", new Item("v"), null, Ct);

        _store.CreatePolicies.Should().Equal(null, WriteFormats.Envelope, WriteFormats.MessagePackPolicy);
    }

    // The server applied the fill and the connection dropped before the answer: the library answers a failure, which proves nothing about the fill.
    [Fact]
    public async Task A_fill_whose_connection_drops_after_the_send_is_unknown_and_one_that_never_left_is_not_sent()
    {
        var (cache, script) = ScriptedCache.Create();
        script.Answers["TryFillAsync"] = _ =>
        {
            _connection.IsConnected = false;
            return new ValueTask<FillResult<Item>>(default(FillResult<Item>));
        };
        var client = new LibraryPathClient<Item>(_store, _connection, key => key, Settle) { Cache = cache };

        var sent = await client.FillAsync("k", new Item("v"), null, null, Ct);
        var unsent = await client.FillAsync("k", new Item("v"), null, null, Ct);

        sent.Outcome.Should().Be(WriteOutcome.Unknown);
        sent.SettleWithin.Should().Be(Settle);
        unsent.Outcome.Should().Be(WriteOutcome.NotSent, "the connection was already down before this one");
    }

    private LibraryPathClient<Item> Client(Func<string, string>? redisKey = null) =>
        new(_store, _connection, redisKey ?? (key => key), Settle);
}
