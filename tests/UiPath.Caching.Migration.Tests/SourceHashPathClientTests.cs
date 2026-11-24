using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

public sealed class SourceHashPathClientTests : RouterTestBase
{
    private readonly Dictionary<string, StoredHash> _store = [];

    [Fact]
    public async Task A_source_that_throws_is_unavailable_for_a_read_and_unknown_for_a_write_and_never_throws()
    {
        var source = Source();
        var client = new SourceHashPathClient<Item>(source, CacheName);
        source.Failure = new InvalidOperationException("down");
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };

        (await client.ReadAsync("k", null, Ct)).Kind.Should().Be(ReadOutcome.Unavailable);
        (await client.SetAsync("k", fields, new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        (await client.RemoveAsync("k", Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        (await client.RemoveFieldsAsync("k", ["a"], Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
        (await client.FillAsync("k", fields, new HashWriteOptions(HashCacheSetOption.KeyReplace, null), null, Ct)).Outcome.Should().Be(WriteOutcome.Unknown);
    }

    [Fact]
    public async Task A_cancellation_of_the_callers_token_is_not_swallowed()
    {
        var source = Source();
        var client = new SourceHashPathClient<Item>(source, CacheName);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        source.Failure = new OperationCanceledException(cts.Token);

        var read = async () => await client.ReadAsync("k", null, cts.Token);
        var write = async () => await client.RemoveAsync("k", cts.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
        await write.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task An_empty_dictionary_is_absent_and_a_hash_is_found_with_only_the_fields_asked_for()
    {
        var source = Source();
        var client = new SourceHashPathClient<Item>(source, CacheName);
        _store["empty"] = new StoredHash();
        _store["full"] = new StoredHash { Fields = new Dictionary<string, Item?> { ["a"] = new Item("1"), ["b"] = new Item("2") } };

        (await client.ReadAsync("empty", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);
        (await client.ReadAsync("none", null, Ct)).Kind.Should().Be(ReadOutcome.Absent);
        var subset = await client.ReadAsync("full", ["b", "x"], Ct);
        subset.Kind.Should().Be(ReadOutcome.Found);
        subset.Fields!.Keys.Should().Equal("b");
        subset.Expiration.Should().BeNull("the source reports no expiry");
        (await client.ReadAsync("full", null, Ct)).Fields.Should().HaveCount(2);
        source.ReadOverride = ReadOutcome.Unreadable;
        (await client.ReadAsync("full", null, Ct)).Kind.Should().Be(ReadOutcome.Unreadable, "the other kinds of a source read map straight through");
    }

    [Fact]
    public async Task A_write_that_carries_metadata_is_refused_before_the_source_is_asked()
    {
        var source = Source();
        var client = new SourceHashPathClient<Item>(source, CacheName);
        var options = new HashWriteOptions(HashCacheSetOption.KeyReplace, null, new Dictionary<string, string?> { ["m"] = "x" });

        var set = async () => await client.SetAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, options, Ct);
        var fill = async () => await client.FillAsync("k", new Dictionary<string, Item?> { ["a"] = new Item("1") }, options, null, Ct);

        await set.Should().ThrowAsync<NotSupportedException>().WithMessage($"*{CacheName}*");
        await fill.Should().ThrowAsync<NotSupportedException>();
        source.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_write_hands_the_source_its_mode_and_its_deadline_and_a_fill_is_a_whole_replace()
    {
        var source = Source();
        var client = new SourceHashPathClient<Item>(source, CacheName);
        var deadline = Clock.GetUtcNow().AddMinutes(3);
        IDictionary<string, Item?> fields = new Dictionary<string, Item?> { ["a"] = new Item("1") };

        await client.SetAsync("k", fields, new HashWriteOptions(HashCacheSetOption.HashReplace, deadline), Ct);
        source.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.HashReplace, deadline));
        await client.SetAsync("k", fields, new HashWriteOptions(HashCacheSetOption.KeyReplace, null), Ct);
        source.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.KeyReplace, null));
        await client.FillAsync("fill", fields, new HashWriteOptions(HashCacheSetOption.HashReplace, deadline), new HashFillBox<Item>(), Ct);
        source.LastOptions.Should().Be(new HashWriteOptions(HashCacheSetOption.KeyReplace, deadline));
        await client.RemoveFieldsAsync("k", ["a"], Ct);
        source.LastRemovedFields.Should().Equal("a");
    }

    [Fact]
    public async Task A_presence_check_is_not_answered_and_an_expiry_and_both_refreshes_are_refused()
    {
        var client = new SourceHashPathClient<Item>(Source(), CacheName);

        (await client.ContainsAsync("k", Ct)).Should().BeNull();
        client.SettleWithin.Should().BeNull();
        var refused = new Func<Task>[]
        {
            async () => await client.ExpireTimeAsync("k", Ct),
            async () => await client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), Ct),
            async () => await client.RefreshAsync("k", CacheExpiry.After(TimeSpan.FromMinutes(1)), null, Ct),
        };
        foreach (var call in refused)
        {
            await call.Should().ThrowAsync<NotSupportedException>().WithMessage($"*{CacheName}*");
        }
    }

    private FakeHashBackend Source() => new(_store, Clock);
}
