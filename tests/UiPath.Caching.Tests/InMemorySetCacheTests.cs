using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Locking;

namespace UiPath.Caching.Tests;

// The InMemory provider's cache: MultilayerSetCache over NullSetCache, where the multilayer's local
// tier is the storage — the set analog of InMemoryCacheProvider serving MultilayerCache over NullCache.
public class InMemorySetCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MultilayerSetCache CreateSut(InMemoryQueueCacheOptions? options = null)
    {
        options ??= new InMemoryQueueCacheOptions();
        return new MultilayerSetCache(
            KnownCacheProviderNames.InMemory, NullSetCache.Instance,
            new MemoryCacheFactory(null, NullLoggerFactory.Instance),
            new SystemJsonSerializerProxy(), options,
            NullLocalLock.Instance,
            localMaxExpiration: null,
            defaultExpiration: options.DefaultExpiration);
    }

    // Casts to IEnumerable<string> so the call binds to the IEnumerable<T> AddAsync overload rather
    // than the single-item AddAsync<T>(..., T item, ...) overload (T = string[]).
    private static ValueTask<long> AddMany(MultilayerSetCache sut, CacheKey key, params string[] items) =>
        sut.AddAsync(key, (IEnumerable<string>)items, (CachePolicy?)null, Ct);

    [Fact]
    public void Name_is_InMemory() => CreateSut().Name.Should().Be("InMemory");

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
    public async Task Add_with_past_expiration_stores_nothing()
    {
        var sut = CreateSut();

        var added = await sut.AddAsync("k", new[] { "a" }, TimeSpan.FromSeconds(-1), null, Ct);

        added.Should().Be(0);
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
