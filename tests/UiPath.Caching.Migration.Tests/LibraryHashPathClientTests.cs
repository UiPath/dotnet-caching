using UiPath.Caching.Migration.Tests.Support;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration.Tests;

public sealed class LibraryHashPathClientTests : RouterTestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(7);

    private readonly FakeMigrationHashCache _store = new();
    private readonly FakeConnection _connection = new();

    [Fact]
    public async Task A_read_that_fails_is_unavailable_and_never_absent()
    {
        _store.Read = (_, _) => throw new TimeoutException("redis timeout");

        (await Client().ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unavailable);
    }

    [Fact]
    public async Task A_read_while_disconnected_is_unavailable_without_asking_the_library()
    {
        _connection.IsConnected = false;

        (await Client().ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unavailable);
        _store.Keys.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SnapshotOutcome.Absent, ReadOutcome.Absent)]
    [InlineData(SnapshotOutcome.Unreadable, ReadOutcome.Unreadable)]
    [InlineData(SnapshotOutcome.Failed, ReadOutcome.Unavailable)]
    public async Task A_snapshot_that_found_nothing_is_what_it_says_and_a_failed_one_is_never_a_miss(SnapshotOutcome snapshot, ReadOutcome expected)
    {
        _store.Read = (_, _) => Task.FromResult<object?>(snapshot);

        (await Client().ReadAsync("k", null, Ct)).Kind.Should().Be(expected);
    }

    [Fact]
    public async Task The_key_is_the_mapped_text_verbatim()
    {
        await Client(key => $" N:{key} ").ReadAsync("K", null, Ct);

        _store.Keys.Single().Name.Should().Be(" N:K ");
    }

    [Fact]
    public async Task A_found_hash_carries_the_ttl_of_the_same_snapshot_and_a_read_of_some_fields_keeps_those()
    {
        _store.Read = (_, _) => Task.FromResult<object?>(new Dictionary<string, Item?> { ["a"] = new("1"), ["b"] = new("2") });

        var all = await Client().ReadAsync("k", null, Ct);
        var some = await Client().ReadAsync("k", ["b", "zz"], Ct);

        all.Kind.Should().Be(ReadOutcome.Found);
        all.Fields!.Count.Should().Be(2);
        all.Expiration.Should().NotBeNull();
        some.Fields!.Keys.Should().BeEquivalentTo("b");
    }

    [Fact]
    public async Task A_hash_that_is_cached_empty_is_a_miss_that_is_the_answer()
    {
        _store.Read = (_, _) => Task.FromResult<object?>(new Dictionary<string, Item?>());

        var read = await Client().ReadAsync("k", null, Ct);

        read.Kind.Should().Be(ReadOutcome.Absent);
        read.Stops.Should().BeTrue();
        read.FallsThrough.Should().BeFalse();
    }

    [Theory]
    [InlineData(StoreOutcome.Unknown)]
    public async Task A_write_that_may_have_been_sent_is_unknown_and_carries_the_settle_deadline(StoreOutcome stored)
    {
        _store.Write = (_, _) => Task.FromResult(stored);
        var client = Client();

        foreach (var result in new[]
        {
            await client.SetAsync("k", Fields(), Options(), Ct),
            await client.RemoveAsync("k", Ct),
            await client.RemoveFieldsAsync("k", ["a"], Ct),
            await client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), null, Ct),
        })
        {
            result.Outcome.Should().Be(WriteOutcome.Unknown);
            result.SettleWithin.Should().Be(Settle);
        }
    }

    [Fact]
    public async Task A_committed_write_is_committed_and_one_refused_before_sending_is_not_sent_and_one_that_throws_is_unknown()
    {
        var client = Client();
        (await client.SetAsync("k", Fields(), Options(), Ct)).Outcome.Should().Be(WriteOutcome.Committed);

        _store.Write = (_, _) => Task.FromResult(StoreOutcome.NotSent);
        (await client.SetAsync("k", Fields(), Options(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        _store.Write = (_, _) => throw new TimeoutException("redis timeout");
        var thrown = await client.RemoveAsync("k", Ct);
        thrown.Outcome.Should().Be(WriteOutcome.Unknown);
        thrown.SettleWithin.Should().Be(Settle);
    }

    [Fact]
    public async Task A_write_while_disconnected_or_cancelled_or_with_a_key_map_that_throws_is_not_sent()
    {
        var client = Client();
        _connection.IsConnected = false;
        (await client.SetAsync("k", Fields(), Options(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        _connection.IsConnected = true;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        (await client.RemoveFieldsAsync("k", ["a"], cts.Token)).Outcome.Should().Be(WriteOutcome.NotSent);
        (await Client(_ => throw new InvalidOperationException("no such tenant")).SetAsync("k", Fields(), Options(), Ct)).Outcome.Should().Be(WriteOutcome.NotSent);

        _store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task A_write_hands_the_deadline_and_the_set_option_to_the_library_and_no_deadline_is_no_expiry()
    {
        var client = Client();
        var deadline = DateTimeOffset.UtcNow.AddHours(1);

        await client.SetAsync("k", Fields(), new HashWriteOptions(HashCacheSetOption.HashReplace, deadline), Ct);
        await client.SetAsync("k", Fields(), new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct);
        await client.RefreshAsync("k", CacheExpiry.At(deadline), null, Ct);

        _store.Calls[0].Options.Should().Be(new HashCacheEntryOptions(deadline, null, null, HashCacheSetOption.HashReplace));
        _store.Calls[1].Options!.Value.ExpireTime.Should().Be(DateTimeOffset.MaxValue);
        _store.Calls[2].Options!.Value.ExpireTime.Should().Be(deadline);
    }

    [Theory]
    [InlineData(StoreOutcome.Committed, WriteOutcome.Committed)]
    [InlineData(StoreOutcome.NotSent, WriteOutcome.NotSent)]
    [InlineData(StoreOutcome.Unknown, WriteOutcome.Unknown)]
    [InlineData(StoreOutcome.Failed, WriteOutcome.NotSent)]
    public async Task The_expiry_only_refresh_is_the_librarys_expiry_only_renew_and_its_outcome_is_the_stores(StoreOutcome stored, WriteOutcome expected)
    {
        _store.Write = (_, _) => Task.FromResult(stored);
        var deadline = DateTimeOffset.UtcNow.AddHours(1);

        var result = await Client().RefreshAsync("k", CacheExpiry.At(deadline), Ct);

        result.Outcome.Should().Be(expected);
        _store.Calls.Should().ContainSingle().Which.Should().Be(("expiry", new HashCacheEntryOptions(deadline, null)));
    }

    // The store answered and applied nothing: the hash is as it was, so the write was not sent. What is unknown stays unknown.
    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("fields")]
    [InlineData("renew")]
    [InlineData("expiry")]
    public async Task A_store_that_answered_and_applied_nothing_is_not_sent_for_every_write_and_one_that_may_have_applied_is_unknown(string operation)
    {
        var client = Client();
        async Task<WriteResult> RunAsync() => operation switch
        {
            "write" => await client.SetAsync("k", Fields(), Options(), Ct),
            "delete" => await client.RemoveAsync("k", Ct),
            "fields" => await client.RemoveFieldsAsync("k", ["a"], Ct),
            "renew" => await client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), null, Ct),
            _ => await client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), Ct),
        };

        _store.Write = (_, _) => Task.FromResult(StoreOutcome.Failed);
        (await RunAsync()).Outcome.Should().Be(WriteOutcome.NotSent);

        _store.Write = (_, _) => Task.FromResult(StoreOutcome.Unknown);
        var unknown = await RunAsync();
        unknown.Outcome.Should().Be(WriteOutcome.Unknown);
        unknown.SettleWithin.Should().Be(Settle);
    }

    // The server applied the fill and the connection dropped before the answer: the library swallows the failure and says nothing was written and nothing existed, which proves nothing about the fill.
    [Fact]
    public async Task A_fill_whose_connection_drops_after_the_send_is_unknown_and_one_that_never_left_is_not_sent()
    {
        var tier = InterceptingProxy.Wrap<ISharedHashTier>(null!, (method, _) =>
        {
            _connection.IsConnected = false;
            return method.Name == nameof(ISharedHashTier.FillVersionedAsync) ? new ValueTask<VersionedWrite>(new VersionedWrite(false, 0, null)) : InterceptingProxy.Proceed;
        });
        var client = new LibraryHashPathClient<Item>(_store, _connection, key => key, Settle) { Tier = tier };

        var sent = await client.FillAsync("k", Fields(), Options(), null, Ct);
        var unsent = await client.FillAsync("k", Fields(), Options(), null, Ct);

        sent.Outcome.Should().Be(WriteOutcome.Unknown);
        sent.SettleWithin.Should().Be(Settle);
        unsent.Outcome.Should().Be(WriteOutcome.NotSent, "the connection was already down before this one");
    }

    [Fact]
    public async Task A_fill_whose_tier_throws_is_unknown_and_is_never_replayed()
    {
        var calls = 0;
        var tier = InterceptingProxy.Wrap<ISharedHashTier>(null!, (_, _) =>
        {
            calls++;
            throw new TimeoutException("redis timeout");
        });
        var client = new LibraryHashPathClient<Item>(_store, _connection, key => key, Settle) { Tier = tier };

        var result = await client.FillAsync("k", Fields(), Options(), null, Ct);

        result.Outcome.Should().Be(WriteOutcome.Unknown);
        result.SettleWithin.Should().Be(Settle);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_presence_check_is_the_librarys_answer_and_is_unavailable_when_the_library_cannot_say()
    {
        var present = true;
        var cache = InterceptingProxy.Wrap<IHashCache>(null!, (method, _) => method.Name == nameof(IHashCache.ContainsAsync) ? (present ? new ValueTask<bool>(true) : throw new TimeoutException("redis timeout")) : InterceptingProxy.Proceed);
        var client = new LibraryHashPathClient<Item>(_store, _connection, key => $"N:{key}", Settle) { Cache = cache };

        (await Client().ContainsAsync("k", Ct)).Should().BeNull("with no library cache there is nothing to ask");
        (await client.ContainsAsync("k", Ct)).Should().BeTrue();
        present = false;
        var failed = await FluentActions.Awaiting(async () => await client.ContainsAsync("k", Ct)).Should().ThrowAsync<CacheUnavailableException>();
        failed.Which.InnerException.Should().BeOfType<TimeoutException>();
        _connection.IsConnected = false;
        await FluentActions.Awaiting(async () => await client.ContainsAsync("k", Ct)).Should().ThrowAsync<CacheUnavailableException>();
    }

    [Fact]
    public async Task An_expiry_is_the_librarys_answer_and_none_when_it_cannot_say_or_there_is_no_connection()
    {
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var broken = false;
        var cache = InterceptingProxy.Wrap<IHashCache>(null!, (method, _) => method.Name == nameof(IHashCache.ExpireTimeAsync) ? (broken ? throw new TimeoutException("redis timeout") : new ValueTask<DateTimeOffset?>(expires)) : InterceptingProxy.Proceed);
        var client = new LibraryHashPathClient<Item>(_store, _connection, key => key, Settle) { Cache = cache };

        await FluentActions.Awaiting(async () => await Client().ExpireTimeAsync("k", Ct)).Should().ThrowAsync<NotSupportedException>();
        (await client.ExpireTimeAsync("k", Ct)).Should().Be(expires);
        broken = true;
        (await client.ExpireTimeAsync("k", Ct)).Should().BeNull();
        _connection.IsConnected = false;
        (await client.ExpireTimeAsync("k", Ct)).Should().BeNull();
    }

    [Fact]
    public void The_client_keeps_the_stored_hash_of_a_lost_fill_and_settles_within_the_bound_it_was_given()
    {
        var client = Client();

        client.KeepsStoredOnFill.Should().BeTrue();
        client.SettleWithin.Should().Be(Settle);
    }

    private static Dictionary<string, Item?> Fields() => new() { ["a"] = new Item("1") };

    private static HashWriteOptions Options() => new(HashCacheSetOption.KeyReplace, null);

    private LibraryHashPathClient<Item> Client(Func<string, string>? redisKey = null) =>
        new(_store, _connection, redisKey ?? (key => key), Settle);
}
