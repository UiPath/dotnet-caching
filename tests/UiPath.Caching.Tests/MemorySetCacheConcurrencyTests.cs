using Microsoft.Extensions.Caching.Memory;

namespace UiPath.Caching.Tests;

/// <summary>Writers of one key exclude each other: the set is replaced as a whole, so a lost update shows as a missing or duplicated member.</summary>
public class MemorySetCacheConcurrencyTests
{
    private const string Key = "k";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Parallel_adds_of_distinct_items_keep_every_item_and_count_each_once()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);
        const int Items = 400;

        var added = await Task.WhenAll(Enumerable.Range(0, Items).Select(i => Task.Run(async () => await sut.AddAsync(Key, [i], null, Ct), Ct)));

        added.Sum().Should().Be(Items);
        sut.TryGetMembers<int>(Key, out var members).Should().BeTrue();
        members!.Order().Should().Equal(Enumerable.Range(0, Items));
    }

    [Fact]
    public async Task Parallel_adds_of_the_same_item_count_one_win()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);

        var added = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () => await sut.AddAsync(Key, [7], null, Ct), Ct)));

        added.Sum().Should().Be(1);
    }

    [Fact]
    public async Task Parallel_pops_return_every_member_exactly_once_between_them()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);
        const int Items = 600;
        await sut.ReplaceAsync(Key, Enumerable.Range(0, Items), null, Ct);

        async Task<List<int>> Drain()
        {
            var mine = new List<int>();
            while (true)
            {
                var batch = await sut.PopAsync<int>(Key, 7, Ct);
                if (batch.Count == 0)
                {
                    return mine;
                }

                mine.AddRange(batch.Select(x => x!));
            }
        }

        var popped = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(Drain, Ct)));

        popped.SelectMany(x => x).Order().Should().Equal(Enumerable.Range(0, Items));
        sut.TryGetMembers<int>(Key, out _).Should().BeFalse("an emptied set leaves no entry");
    }

    [Fact]
    public async Task Adds_and_removes_interleaved_end_with_exactly_what_was_left()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);
        await sut.ReplaceAsync(Key, Enumerable.Range(0, 200), null, Ct);

        var removes = Enumerable.Range(0, 100).Select(i => Task.Run(async () => await sut.RemoveAsync(Key, [i], Ct), Ct));
        var adds = Enumerable.Range(200, 100).Select(i => Task.Run(async () => await sut.AddAsync(Key, [i], null, Ct), Ct));
        var results = await Task.WhenAll(removes.Concat(adds));

        results.Sum().Should().Be(200, "each of 100 removes and 100 adds changed the set once");
        sut.TryGetMembers<int>(Key, out var members).Should().BeTrue();
        members!.Order().Should().Equal(Enumerable.Range(100, 200));
    }

    [Fact]
    public async Task Removing_the_key_while_adding_never_resurrects_a_member_that_the_remove_saw()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);

        for (var round = 0; round < 50; round++)
        {
            await sut.ReplaceAsync(Key, [1, 2, 3], null, Ct);
            var remove = Task.Run(async () => await sut.RemoveKeyAsync(Key, Ct), Ct);
            var add = Task.Run(async () => await sut.AddAsync(Key, [4], null, Ct), Ct);
            await Task.WhenAll(remove, add);

            // Either order is possible; a half-done one is not: the set holds all of {1,2,3,4}, or only {4}, or nothing.
            if (sut.TryGetMembers<int>(Key, out var members))
            {
                var seen = members!.Order().ToArray();
                (seen.SequenceEqual([1, 2, 3, 4]) || seen.SequenceEqual([4])).Should().BeTrue("the set holds all of 1 to 4, or only 4, not part of them");
            }
        }
    }

    [Fact]
    public async Task Different_keys_proceed_while_one_key_is_busy()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);

        var added = await Task.WhenAll(Enumerable.Range(0, 300).Select(i => Task.Run(async () => await sut.AddAsync("key-" + i, [i], null, Ct), Ct)));

        added.Sum().Should().Be(300);
        for (var i = 0; i < 300; i++)
        {
            sut.TryGetCount("key-" + i, out var count).Should().BeTrue();
            count.Should().Be(1);
        }
    }

    [Fact]
    public async Task A_cancelled_call_changes_nothing_and_reports_its_cancellation()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);
        await sut.AddAsync(Key, [1], null, Ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions.Awaiting(async () => await sut.ReplaceAsync(Key, [2], null, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await sut.AddAsync(Key, [2], cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await sut.AddAsync(Key, [2], null, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await sut.RemoveAsync(Key, [1], cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await sut.RemoveKeyAsync(Key, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(async () => await sut.PopAsync<int>(Key, 1, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();

        sut.TryGetMembers<int>(Key, out var members).Should().BeTrue();
        members.Should().Equal(1);
    }

    [Fact]
    public async Task Calls_with_nothing_to_do_return_at_once_and_change_nothing()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var sut = Create(memory);
        await sut.AddAsync(Key, [1], null, Ct);

        await sut.AddAsync(Key, Array.Empty<int>(), Ct);
        (await sut.AddAsync(Key, Array.Empty<int>(), null, Ct)).Should().Be(0);
        (await sut.AddAsync(Key, [2], DateTimeOffset.UtcNow.AddMinutes(-1), Ct)).Should().Be(0, "a deadline already past stores nothing and drops the key");
        (await sut.RemoveAsync(Key, Array.Empty<int>(), Ct)).Should().Be(0);
        (await sut.RemoveAsync("missing", [1], Ct)).Should().Be(0);
        (await sut.PopAsync<int>(Key, 0, Ct)).Should().BeEmpty();
        (await sut.PopAsync<int>("missing", 1, Ct)).Should().BeEmpty();

        sut.ContainsKey(Key).Should().BeFalse("the past deadline removed the key");
    }

    private static MemorySetCache Create(IMemoryCache memory) =>
        new(memory, new SystemJsonByteSerializerProxy(), new InMemoryQueueCacheOptions(), TimeProvider.System);
}
