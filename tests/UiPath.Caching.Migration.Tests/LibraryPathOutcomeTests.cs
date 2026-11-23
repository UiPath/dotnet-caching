using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// An answer lost after the send cannot establish whether this call wrote the value.
public sealed class LibraryPathOutcomeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_create_the_library_answers_false_for_is_always_unknown_whatever_a_read_finds_because_a_value_proves_nothing_about_who_wrote_it()
    {
        var (client, script, store) = Client();
        var created = false;
        script.Answers["TryAddAsync"] = _ => new ValueTask<bool>(created);

        // The create may have committed, lost its reply and returned false: the value the read finds can be its own.
        foreach (var held in new object?[] { new Item("maybe-mine"), null, SnapshotOutcome.Failed })
        {
            store.Read = (_, _) => Task.FromResult(held);

            var result = await client.TryAddAsync("k", new Item("mine"), null, Ct);

            result.Kind.Should().Be(TryAddKind.Failed);
            result.Failure.Outcome.Should().Be(WriteOutcome.Unknown);
        }

        created = true;
        (await client.TryAddAsync("k", new Item("mine"), null, Ct)).Kind.Should().Be(TryAddKind.Created);
    }

    [Fact]
    public async Task A_swap_the_library_answers_failed_for_is_unknown_and_an_unchanged_one_is_not_sent()
    {
        var (client, script, _) = Client();

        script.Answers["SetIfChangedAsync"] = _ => new ValueTask<ChangeResult<Item>>(new ChangeResult<Item>(ChangeOutcome.Failed, null));
        (await client.SetIfChangedAsync("k", new Item("v"), EqualityComparer<Item>.Default, null, Ct)).Write.Outcome.Should().Be(WriteOutcome.Unknown);

        script.Answers["SetIfChangedAsync"] = _ => new ValueTask<ChangeResult<Item>>(new ChangeResult<Item>(ChangeOutcome.Unchanged, new Item("v")));
        (await client.SetIfChangedAsync("k", new Item("v"), EqualityComparer<Item>.Default, null, Ct)).Write.Outcome.Should().Be(WriteOutcome.NotSent);

        script.Answers["SetIfChangedAsync"] = _ => new ValueTask<ChangeResult<Item>>(new ChangeResult<Item>(ChangeOutcome.Replaced, new Item("v")));
        (await client.SetIfChangedAsync("k", new Item("v"), EqualityComparer<Item>.Default, null, Ct)).Write.Outcome.Should().Be(WriteOutcome.Committed);
    }

    [Fact]
    public async Task A_cancellation_of_the_swap_and_an_argument_the_library_refuses_propagate()
    {
        var (client, script, _) = Client();
        script.Answers["SetIfChangedAsync"] = _ => throw new OperationCanceledException();

        var cancelled = async () => await client.SetIfChangedAsync("k", new Item("v"), EqualityComparer<Item>.Default, null, Ct);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        script.Answers["SetIfChangedAsync"] = args => RefuseExpiration(args[3]);
        var refused = async () => await client.SetIfChangedAsync("k", new Item("v"), EqualityComparer<Item>.Default, null, Ct);
        await refused.Should().ThrowAsync<ArgumentOutOfRangeException>();

        static object RefuseExpiration(object? expiration) =>
            throw new ArgumentOutOfRangeException(nameof(expiration), expiration, "The library refused the expiration.");
    }

    [Fact]
    public async Task A_refresh_the_library_answers_false_for_is_always_unknown_even_when_the_key_is_absent_afterwards()
    {
        var (client, script, store) = Client();
        var refreshed = false;
        script.Answers["RefreshAsync"] = _ => new ValueTask<bool>(refreshed);

        // A shorter life can commit, lose its reply and expire before the read.
        foreach (var held in new object?[] { null, new Item("v") })
        {
            store.Read = (_, _) => Task.FromResult(held);
            (await client.RefreshAsync("k", default, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        }

        refreshed = true;
        (await client.RefreshAsync("k", default, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
    }

    [Fact]
    public async Task A_fill_is_the_library_fill_so_it_never_writes_over_a_stored_value_and_hands_the_winner_back()
    {
        var (client, script, _) = Client();
        var calls = 0;
        script.Answers["SetAsync"] = _ => throw new InvalidOperationException("a fill must not be an unconditional set");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

        script.Answers["TryFillAsync"] = _ => { calls++; return new ValueTask<FillResult<Item>>(new FillResult<Item>(FillOutcome.Existed, new Item("winner"), expiry)); };
        var box = new FillBox<Item>();
        (await client.FillAsync("k", new Item("mine"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        box.Stored.Should().Be(new Item("winner"));
        box.StoredExpiration.Should().Be(expiry);

        script.Answers["TryFillAsync"] = _ => new ValueTask<FillResult<Item>>(new FillResult<Item>(FillOutcome.Unreadable, null, null));
        box = new FillBox<Item>();
        (await client.FillAsync("k", new Item("mine"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        box.Unreadable.Should().BeTrue();

        script.Answers["TryFillAsync"] = _ => new ValueTask<FillResult<Item>>(new FillResult<Item>(FillOutcome.Added, null, null));
        (await client.FillAsync("k", new Item("mine"), null, null, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_missing_connection_refuses_operations_before_the_library_is_called()
    {
        var connection = new FakeConnection { IsConnected = false };
        var (cache, script) = ScriptedCache.Create();
        var client = new LibraryPathClient<Item>(new FakeMigrationCache(), connection, k => k, TimeSpan.FromSeconds(4)) { Cache = cache };

        var add = await client.TryAddAsync("k", null, null, Ct);
        add.Kind.Should().Be(TryAddKind.Failed);
        add.Failure.Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.SetIfChangedAsync("k", null, EqualityComparer<Item>.Default, null, Ct)).Write.Outcome.Should().Be(WriteOutcome.NotSent);
        (await client.TryFillAsync("k", null, null, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await client.TimeToLiveAsync("k", Ct)).Should().BeNull();
        (await client.ExpireTimeAsync("k", Ct)).Should().BeNull();
        await Assert.ThrowsAsync<CacheUnavailableException>(async () => await client.ContainsAsync("k", Ct));
        await Assert.ThrowsAsync<CacheUnavailableException>(async () => await client.RefreshAsync("k", default, Ct));
        script.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_without_the_library_cache_fills_through_its_store_and_cannot_answer_expiry()
    {
        var store = new FakeMigrationCache();
        var client = new LibraryPathClient<Item>(store, new FakeConnection(), k => $"mapped:{k}", TimeSpan.FromSeconds(4));
        var box = new FillBox<Item>();

        client.PlainBatch.Should().BeNull();
        (await client.ContainsAsync("k", Ct)).Should().BeNull();
        (await client.FillAsync("k", new Item("value"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        box.Lost.Should().BeFalse();
        store.Keys.Should().ContainSingle().Which.Name.Should().Be("mapped:k");
        await Assert.ThrowsAsync<NotSupportedException>(async () => await client.TimeToLiveAsync("k", Ct));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await client.ExpireTimeAsync("k", Ct));
    }

    [Fact]
    public async Task A_fill_with_no_result_box_preserves_the_library_winner_even_when_it_is_null()
    {
        var (client, script, store) = Client();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(2);
        script.Answers["TryFillAsync"] = _ => new FillResult<Item>(FillOutcome.Existed, null, expiry);

        (await client.FillAsync("k", new Item("new"), null, null, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        var box = new FillBox<Item>();
        (await client.FillAsync("k", new Item("again"), null, box, Ct)).Outcome.Should().Be(WriteOutcome.NotSent);
        box.CachedNull.Should().BeTrue();
        box.Lost.Should().BeTrue();
        box.StoredExpiration.Should().Be(expiry);
        store.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task Command_failures_are_reported_as_unknown_writes_or_unavailable_reads()
    {
        var (client, script, _) = Client();
        var error = new TimeoutException("Lost reply");
        string[] commands = ["TryAddAsync", "SetIfChangedAsync", "TryFillAsync", "TimeToLiveAsync", "ExpireTimeAsync", "RefreshAsync", "ContainsAsync"];
        foreach (var name in commands)
        {
            script.Answers[name] = _ => throw error;
        }

        var add = await client.TryAddAsync("k", null, null, Ct);
        add.Kind.Should().Be(TryAddKind.Failed);
        add.Failure.Should().Be(WriteResult.UnknownWithin(TimeSpan.FromSeconds(30)));
        (await client.SetIfChangedAsync("k", null, EqualityComparer<Item>.Default, null, Ct)).Write.Should().Be(add.Failure);
        (await client.TryFillAsync("k", null, null, Ct)).Outcome.Should().Be(FillOutcome.Failed);
        (await client.RefreshAsync("k", default, Ct)).Should().Be(add.Failure);
        (await client.TimeToLiveAsync("k", Ct)).Should().BeNull();
        (await client.ExpireTimeAsync("k", Ct)).Should().BeNull();
        var unavailable = await Assert.ThrowsAsync<CacheUnavailableException>(async () => await client.ContainsAsync("k", Ct));
        unavailable.InnerException.Should().BeSameAs(error);
    }

    [Fact]
    public async Task Presence_and_expiration_are_forwarded_and_refresh_uses_the_requested_overload()
    {
        var (client, script, _) = Client();
        var span = TimeSpan.FromMinutes(2);
        var deadline = DateTimeOffset.UtcNow + span;
        script.Answers["TimeToLiveAsync"] = args =>
        {
            ((CacheKey)args[0]!).Name.Should().Be("hit");
            return span;
        };
        script.Answers["ExpireTimeAsync"] = args => ((CacheKey)args[0]!).Name == "hit" ? deadline : null;
        script.Answers["ContainsAsync"] = args => ((CacheKey)args[0]!).Name == "hit";
        var requested = new List<object?>();
        script.Answers["RefreshAsync"] = args =>
        {
            ((CacheKey)args[0]!).Name.Should().Be("hit");
            requested.Add(args[1]);
            return true;
        };

        (await client.TimeToLiveAsync("hit", Ct)).Should().Be(span);
        (await client.ExpireTimeAsync("hit", Ct)).Should().Be(deadline);
        (await client.ExpireTimeAsync("missing", Ct)).Should().BeNull();
        (await client.ContainsAsync("hit", Ct)).Should().BeTrue();
        (await client.ContainsAsync("missing", Ct)).Should().BeFalse();
        (await client.RefreshAsync("hit", CacheExpiry.At(deadline), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        (await client.RefreshAsync("hit", CacheExpiry.After(span), Ct)).Outcome.Should().Be(WriteOutcome.Committed);
        requested.Should().Equal(deadline, span);
    }

    [Fact]
    public async Task A_cancelled_create_is_not_sent_and_a_cancelled_expiry_read_propagates()
    {
        var (client, script, _) = Client();
        var cancelled = new CancellationToken(true);
        var add = await client.TryAddAsync("k", null, null, cancelled);
        add.Failure.Outcome.Should().Be(WriteOutcome.NotSent);
        script.Calls.Should().BeEmpty();

        script.Answers["TimeToLiveAsync"] = args => Task.FromCanceled<TimeSpan?>((CancellationToken)args[^1]!);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await client.TimeToLiveAsync("k", cancelled));
    }

    private static (LibraryPathClient<Item> Client, ScriptedCache Script, FakeMigrationCache Store) Client()
    {
        var (cache, script) = ScriptedCache.Create();
        var store = new FakeMigrationCache();
        return (new LibraryPathClient<Item>(store, new FakeConnection(), key => key, TimeSpan.FromSeconds(30)) { Cache = cache }, script, store);
    }
}
