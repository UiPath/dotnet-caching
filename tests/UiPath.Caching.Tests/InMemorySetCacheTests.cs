using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests;

// The InMemory provider's cache: MultilayerSetCache over NullSetCache, where the multilayer's local
// tier is the storage — the set analog of InMemoryCacheProvider serving MultilayerCache over NullCache.
public class InMemorySetCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Hoisted out of the call below so the array is not rebuilt per invocation (CA1861).
    private static readonly string[] OneItem = ["a"];

    private static MultilayerSetCache CreateSut(InMemoryQueueCacheOptions? options = null, TimeProvider? clock = null)
    {
        options ??= new InMemoryQueueCacheOptions();
        var cacheClock = clock ?? TimeProvider.System;
        return new MultilayerSetCache(
            KnownCacheProviderNames.InMemory, NullSetCache.Instance,
            new MemoryCacheFactory(cacheClock, NullLoggerFactory.Instance),
            new SystemJsonByteSerializerProxy(), options,
            NullLocalLock.Instance, cacheClock);
    }

    // Casts to IEnumerable<string> so the call binds to the IEnumerable<T> AddAsync overload rather
    // than the single-item AddAsync<T>(..., T item, ...) overload (T = string[]).
    private static ValueTask<long> AddMany(MultilayerSetCache sut, CacheKey key, params string[] items) =>
        sut.AddAsync(key, (IEnumerable<string>)items, (CachePolicy?)null, Ct);

    [Fact]
    public void Name_is_InMemory() => CreateSut().Name.Should().Be("InMemory");

    private sealed record Member(int Id, string Name);

    /// <summary>
    /// The snapshot is keyed on the serializer's <c>byte[]</c> output, which compares by reference.
    /// A populated local tier is authoritative, so without structural equality the wrong answer is
    /// never corrected against the backing tier.
    /// </summary>
    /// <summary>
    /// With a passthrough serializer the snapshot would otherwise hold the caller's own array, so
    /// mutating it after the add would change an element's hash from inside the set.
    /// </summary>
    [Fact]
    public async Task A_member_mutated_after_being_added_does_not_corrupt_the_snapshot()
    {
        var sut = new MultilayerSetCache(
            KnownCacheProviderNames.InMemory, NullSetCache.Instance,
            new MemoryCacheFactory(TimeProvider.System, NullLoggerFactory.Instance),
            new RawByteSerializerProxy(), new InMemoryQueueCacheOptions { DefaultExpiration = null },
            NullLocalLock.Instance, TimeProvider.System);
        var payload = new byte[] { 1, 2, 3 };

        (await sut.AddAsync("k", payload, (CachePolicy?)null, Ct)).Should().BeTrue();
        payload[0] = 9;

        (await sut.ContainsItemAsync("k", new byte[] { 1, 2, 3 }, Ct)).Should().BeTrue();
        (await sut.CountAsync<byte[]>("k", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Members_are_matched_by_their_serialized_bytes_not_by_reference()
    {
        var sut = CreateSut();
        var stored = new Member(7, "héllo 世界");
        var equalButDistinctInstance = new Member(7, "héllo 世界");

        (await sut.AddAsync("k", stored, (CachePolicy?)null, Ct)).Should().BeTrue();

        (await sut.ContainsItemAsync("k", equalButDistinctInstance, Ct)).Should().BeTrue();
        (await sut.AddAsync("k", equalButDistinctInstance, (CachePolicy?)null, Ct)).Should().BeFalse();
        (await sut.CountAsync<Member>("k", Ct)).Should().Be(1);
        (await sut.RemoveItemAsync("k", equalButDistinctInstance, Ct)).Should().BeTrue();
        (await sut.CountAsync<Member>("k", Ct)).Should().Be(0);
    }

    /// <summary>The deadline is past by wall-clock time and future by the injected clock; accepted only if every check reads the injected one.</summary>
    [Fact]
    public async Task Expirations_are_resolved_against_the_configured_clock()
    {
        var now = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sut = CreateSut(clock: new FakeTimeProvider(now));

        var added = await sut.AddAsync("k", (IEnumerable<string>)OneItem, now.AddDays(1), (CachePolicy?)null, Ct);

        added.Should().Be(1);
        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEquivalentTo(OneItem);
    }

    [Fact]
    public async Task Add_with_an_unbounded_default_expiration_stores_the_item()
    {
        var sut = CreateSut(new InMemoryQueueCacheOptions { DefaultExpiration = TimeSpan.MaxValue });

        (await sut.AddAsync("k", "a", token: Ct)).Should().BeTrue();

        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEquivalentTo(OneItem);
    }

    [Fact]
    public async Task Add_single_deduplicates()
    {
        var sut = CreateSut();

        (await sut.AddAsync("k", "a", token: Ct)).Should().BeTrue();
        (await sut.AddAsync("k", "a", token: Ct)).Should().BeFalse();

        (await sut.CountAsync<string>("k", Ct)).Should().Be(1);
        (await sut.ContainsItemAsync("k", "a", Ct)).Should().BeTrue();
        (await sut.ContainsAsync<string>("k", Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Add_many_returns_added_count_and_members()
    {
        var sut = CreateSut();

        var added = await AddMany(sut, "k", "a", "b", "a");

        added.Should().Be(2);
        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task Pop_removes_a_random_member()
    {
        var sut = CreateSut();
        await AddMany(sut, "k", "a", "b", "c");

        var popped = await sut.PopAsync<string>("k", token: Ct);

        popped.Should().NotBeNull();
        new[] { "a", "b", "c" }.Should().Contain(popped!);
        (await sut.CountAsync<string>("k", Ct)).Should().Be(2);
        (await sut.ContainsItemAsync("k", popped!, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Pop_count_removes_multiple()
    {
        var sut = CreateSut();
        await AddMany(sut, "k", "a", "b", "c");

        var popped = await sut.PopAsync<string>("k", 2, token: Ct);

        popped.Should().HaveCount(2);
        popped.Should().OnlyHaveUniqueItems();
        (await sut.CountAsync<string>("k", Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Pop_more_than_present_returns_all_and_deletes_key()
    {
        var sut = CreateSut();
        await AddMany(sut, "k", "a", "b");

        var popped = await sut.PopAsync<string>("k", 5, token: Ct);

        popped.Should().HaveCount(2);
        (await sut.ContainsAsync<string>("k", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Pop_on_missing_key_returns_default()
    {
        var sut = CreateSut();

        (await sut.PopAsync<string>("missing", token: Ct)).Should().BeNull();
        (await sut.PopAsync<string>("missing", 3, token: Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveItem_and_RemoveItems()
    {
        var sut = CreateSut();
        await AddMany(sut, "k", "a", "b", "c");

        (await sut.RemoveItemAsync("k", "a", Ct)).Should().BeTrue();
        (await sut.RemoveItemAsync("k", "a", Ct)).Should().BeFalse();
        (await sut.RemoveItemsAsync("k", new[] { "b", "missing" }, Ct)).Should().Be(1);

        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEquivalentTo(new[] { "c" });
    }

    [Fact]
    public async Task Removing_last_member_deletes_the_key()
    {
        var sut = CreateSut();
        await sut.AddAsync("k", "a", token: Ct);

        (await sut.RemoveItemAsync("k", "a", Ct)).Should().BeTrue();

        (await sut.ContainsAsync<string>("k", Ct)).Should().BeFalse();
        (await sut.CountAsync<string>("k", Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Remove_deletes_whole_set()
    {
        var sut = CreateSut();
        await AddMany(sut, "k", "a", "b");

        (await sut.RemoveAsync<string>("k", Ct)).Should().BeTrue();
        (await sut.RemoveAsync<string>("k", Ct)).Should().BeFalse();
        (await sut.MembersAsync<string>("k", token: Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Add_with_past_expiration_is_rejected()
    {
        var sut = CreateSut();

        var act = async () => await sut.AddAsync("k", OneItem, TimeSpan.FromSeconds(-1), null, Ct);

        (await act.Should().ThrowAsync<ArgumentOutOfRangeException>()).And.ParamName.Should().Be("expiration");
        (await sut.ContainsAsync<string>("k", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Reads_on_missing_key_are_empty()
    {
        var sut = CreateSut();

        (await sut.MembersAsync<string>("missing", token: Ct)).Should().BeEmpty();
        (await sut.CountAsync<string>("missing", Ct)).Should().Be(0);
        (await sut.ContainsItemAsync("missing", "a", Ct)).Should().BeFalse();
        (await sut.ContainsAsync<string>("missing", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Null_key_throws()
    {
        var sut = CreateSut();

        var act = async () => await sut.AddAsync(CacheKey.Null, "a", token: Ct);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_can_be_called()
    {
        var sut = CreateSut();
        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }
}
