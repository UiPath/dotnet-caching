using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

#pragma warning disable CA1859 // the cache variables are typed as the interface on purpose, to exercise interface binding

#pragma warning disable xUnit1051 // the shapes under test pass `default` for the token or leave it out

/// <summary>Calls in the shapes callers write compile and reach the closure member, not the state or argument overload beside it.</summary>
[Collection(AllocationCollection.Name)]
public class OverloadShapeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Closure_shapes_on_the_hash_caches_bind_to_the_closure_members()
    {
        var hash = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();
        var generator = new Func<CancellationToken, Task<IDictionary<string, string?>>>(_ => throw new InvalidOperationException());

        await hash.GetOrAddAsync<string>("k", _ => throw new InvalidOperationException(), null, Ct);
        await hash.GetOrAddAsync<string>("k", async _ => default!, TimeSpan.FromMinutes(1), null, Ct);
        await hash.GetOrAddAsync("k", generator, null, Ct);
        await hash.GetOrAddAsync("k", generator, DateTimeOffset.MaxValue, HashCacheSetOption.KeyReplace, null);
        await typed.GetOrAddAsync("k", _ => throw new InvalidOperationException(), Ct);
        await typed.GetOrAddAsync("k", async _ => default!, TimeSpan.FromMinutes(1), Ct);
        await typed.GetOrAddAsync("k", generator, default);
        await typed.GetOrAddAsync((CacheKey)default, generator, Ct);

        await hash.Received(2).GetOrAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), null, Ct);
        await hash.Received(1).GetOrAddAsync<string>(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), TimeSpan.FromMinutes(1), null, Ct);
        await typed.Received(1).GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), TimeSpan.FromMinutes(1), Ct);
        await typed.Received(3).GetOrAddAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<IDictionary<string, string?>>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Closure_shapes_of_GetOrAddWithExpirationAsync_bind_to_the_closure_members()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        ValueTaskStubs.Returns(cache.GetOrAddWithExpirationAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CachePolicy?>(), Arg.Any<CancellationToken>()), "hit");
        ValueTaskStubs.Returns(typed.GetOrAddWithExpirationAsync(Arg.Any<CacheKey>(), Arg.Any<Func<CancellationToken, Task<GeneratedValue<string>>>>(), Arg.Any<CancellationToken>()), "hit");
        var generator = new Func<CancellationToken, Task<GeneratedValue<string>>>(_ => throw new InvalidOperationException());

        (await cache.GetOrAddWithExpirationAsync<string>("k", _ => throw new InvalidOperationException(), null, Ct)).Should().Be("hit");
        (await cache.GetOrAddWithExpirationAsync<string>("k", async _ => default, Ct)).Should().Be("hit");
        (await cache.GetOrAddWithExpirationAsync("k", generator, null)).Should().Be("hit");
        (await typed.GetOrAddWithExpirationAsync("k", _ => throw new InvalidOperationException(), Ct)).Should().Be("hit");
        (await typed.GetOrAddWithExpirationAsync("k", async _ => default, Ct)).Should().Be("hit");
        (await typed.GetOrAddWithExpirationAsync("k", generator, default)).Should().Be("hit");
    }

    [Fact]
    public async Task Batch_closure_shapes_bind_to_the_batch_members_not_the_argument_ones()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var entries = new[] { new KeyValuePair<CacheKey, int>("k", 1) };
        var generator = new Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>((_, _) => throw new InvalidOperationException());

        await cache.GetOrAddAsync<string, int>(entries, (_, _) => throw new InvalidOperationException(), null, Ct);
        await cache.GetOrAddAsync(entries, generator, null, Ct);
        await cache.GetOrAddAsync(entries, generator, TimeSpan.FromMinutes(1), null);
        await typed.GetOrAddAsync(entries, (_, _) => throw new InvalidOperationException(), Ct);
        await typed.GetOrAddAsync(entries, async (_, _) => default!, TimeSpan.FromMinutes(1), Ct);
        await typed.GetOrAddAsync(entries, generator, default);

        await cache.Received(2).GetOrAddAsync(entries, Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), null, Ct);
        await cache.Received(1).GetOrAddAsync(entries, generator, TimeSpan.FromMinutes(1), null, default);
        await typed.Received(2).GetOrAddAsync(entries, Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), Arg.Any<CancellationToken>());
        await typed.Received(1).GetOrAddAsync(entries, Arg.Any<Func<int[], CancellationToken, Task<KeyValuePair<int, string?>[]>>>(), TimeSpan.FromMinutes(1), Ct);
    }

    [Fact]
    public async Task Interpolated_key_binds_to_the_handler_member()
    {
        var recorder = new SpanRecordingCache();
        ICache cache = recorder;
        var (a, b) = ("a", "b");

        await cache.GetOrAddAsync($"{a}:{b}", 1, Generate, (CachePolicy?)null, Ct);

        recorder.SpanKeys.Should().Equal("a:b");
        recorder.GetOrAddKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task Interpolated_key_without_span_capability_falls_back_to_the_CacheKey_member()
    {
        var store = new DictionaryCache();
        ICache cache = store;
        var id = 7;

        var value = await cache.GetOrAddAsync($"a:{id}", 1, Generate, (CachePolicy?)null, Ct);

        value.Should().Be("v");
        store.GetOrAddKeys.Should().Equal(new CacheKey("a:7"));
    }

    [Fact]
    public async Task String_variable_and_const_keys_bind_to_the_CacheKey_member()
    {
        var recorder = new SpanRecordingCache();
        ICache cache = recorder;
        var variable = "a:b";
        const string Constant = "c:d";

        await cache.GetOrAddAsync(variable, 1, Generate, (CachePolicy?)null, Ct);
        await cache.GetOrAddAsync(Constant, 1, Generate, (CachePolicy?)null, Ct);

        recorder.SpanKeys.Should().BeEmpty();
        recorder.GetOrAddKeys.Should().Equal(new CacheKey("a:b"), new CacheKey("c:d"));
    }

    [Fact]
    public async Task Constant_interpolated_key_compiles_and_matches_the_string_key()
    {
        var recorder = new SpanRecordingCache();
        ICache cache = recorder;

        await cache.GetOrAddAsync($"fixed", 1, Generate, (CachePolicy?)null, Ct);
        await cache.GetOrAddAsync("fixed", 1, Generate, (CachePolicy?)null, Ct);

        recorder.SpanKeys.Should().Equal("fixed");
        recorder.GetOrAddKeys.Should().Equal(new CacheKey("fixed"));
    }

    [Fact]
    public async Task String_variable_and_const_keys_bind_to_the_CacheKey_member_of_every_shape()
    {
        var recorder = new SpanRecordingCache();
        ICache cache = recorder;
        var typed = Substitute.For<ICache<string>>();
        var variable = "a:b";
        const string Constant = "c:d";

        await cache.GetAsync<string>(variable, (CachePolicy?)null, Ct);
        await cache.ContainsAsync<string>(Constant, Ct);
        await cache.TimeToLiveAsync<string>(variable, Ct);
        await cache.SetAsync(Constant, "v", (CachePolicy?)null, Ct);
        await typed.GetAsync(variable, Ct);
        await typed.RefreshAsync(Constant, TimeSpan.FromMinutes(1), Ct);

        recorder.Calls.Should().BeEmpty();
        await typed.Received(1).GetAsync(new CacheKey("a:b"), Ct);
        await typed.Received(1).RefreshAsync(new CacheKey("c:d"), TimeSpan.FromMinutes(1), Ct);
    }

    [Fact]
    public async Task Interpolated_and_constant_interpolated_keys_bind_to_the_handler_member_of_every_shape()
    {
        var recorder = new SpanRecordingCache();
        ICache cache = recorder;
        var id = 7;

        await cache.GetAsync<string>($"a:{id}", (CachePolicy?)null, Ct);
        await cache.GetAsync<string>($"fixed", (CachePolicy?)null, Ct);
        await cache.ContainsAsync<string>($"a:{id}", Ct);
        await cache.SetAsync($"a:{id}", "v", TimeSpan.FromMinutes(1), (CachePolicy?)null, Ct);
        await cache.GetOrAddAsync($"a:{id}", async _ => "v", (CachePolicy?)null, Ct);

        recorder.Calls.Select(c => c.Key).Should().Equal("a:7", "fixed", "a:7", "a:7", "a:7");
    }

    [Fact]
    public async Task A_bare_default_key_compiles_against_the_handler_members()
    {
        var cache = Substitute.For<ICache>();

        await cache.GetAsync<string>(default, (CachePolicy?)null, Ct);

        cache.ReceivedCalls().Single().GetOriginalArguments()[0].Should().BeOfType<CacheKey>().Which.IsNull.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(HandlerKeyCacheTests.UntypedTags), MemberType = typeof(HandlerKeyCacheTests))]
    public async Task Interpolated_key_binds_to_the_handler_member_for_every_ICache_shape(string tag)
    {
        var cache = Substitute.For<ICache>();
        var call = HandlerKeyCacheTests.UntypedCases.Single(c => c.Tag == tag).Call;
        var padding = new string('X', 4000);
        await call(cache, padding, 7);

        var allocated = Allocated(() => call(cache, padding, 7));

        allocated.Should().BeLessThan(12_000, "the handler builds the 8000-byte normalized key once; the string path also allocates the interpolated string");
    }

    [Theory]
    [MemberData(nameof(HandlerKeyCacheTests.TypedTags), MemberType = typeof(HandlerKeyCacheTests))]
    public async Task Interpolated_key_binds_to_the_handler_member_for_every_ICache_of_T_shape(string tag)
    {
        var cache = Substitute.For<ICache<string>>();
        var call = HandlerKeyCacheTests.TypedCases.Single(c => c.Tag == tag).Call;
        var padding = new string('X', 4000);
        await call(cache, padding, 7);

        var allocated = Allocated(() => call(cache, padding, 7));

        allocated.Should().BeLessThan(12_000, "the handler builds the 8000-byte normalized key once; the string path also allocates the interpolated string");
    }

    [Theory]
    [MemberData(nameof(HandlerKeyHashCacheTests.UntypedTags), MemberType = typeof(HandlerKeyHashCacheTests))]
    public async Task Interpolated_key_binds_to_the_handler_member_for_every_IHashCache_shape(string tag)
    {
        var cache = HandlerKeyHashCacheTests.Mock(tag);
        var call = HandlerKeyHashCacheTests.UntypedCases.Single(c => c.Tag == tag).Call;
        var padding = new string('X', 4000);
        await call(cache, padding, 7);

        var allocated = Allocated(() => call(cache, padding, 7));

        allocated.Should().BeLessThan(12_000, "the handler builds the 8000-byte normalized key once; the string path also allocates the interpolated string");
    }

    [Theory]
    [MemberData(nameof(HandlerKeyHashCacheTests.TypedTags), MemberType = typeof(HandlerKeyHashCacheTests))]
    public async Task Interpolated_key_binds_to_the_handler_member_for_every_IHashCache_of_T_shape(string tag)
    {
        var cache = HandlerKeyHashCacheTests.TypedMock(tag);
        var call = HandlerKeyHashCacheTests.TypedCases.Single(c => c.Tag == tag).Call;
        var padding = new string('X', 4000);
        await call(cache, padding, 7);

        var allocated = Allocated(() => call(cache, padding, 7));

        allocated.Should().BeLessThan(12_000, "the handler builds the 8000-byte normalized key once; the string path also allocates the interpolated string");
    }

    [Theory]
    [MemberData(nameof(HandlerKeyHashCacheTests.NullTags), MemberType = typeof(HandlerKeyHashCacheTests))]
    public async Task Interpolated_key_binds_to_the_handler_member_of_NullHashCache_and_allocates_no_key(string tag)
    {
        var call = HandlerKeyHashCacheTests.NullCases.Single(c => c.Tag == tag).Call;
        var padding = new string('X', 4000);
        await call(NullHashCache.Instance, padding, 7);

        var allocated = Allocated(() => call(NullHashCache.Instance, padding, 7));

        allocated.Should().BeLessThan(4_000, "the null cache reads no key, so neither the 8000-byte key nor the interpolated string is built");
    }

    [Fact]
    public async Task The_allocation_check_tells_a_string_key_from_a_handler_key()
    {
        var cache = Substitute.For<ICache>();
        var padding = new string('X', 4000);
        await cache.RemoveAsync<string>($"{padding}:{7}", Ct);

        var allocated = Allocated(async () =>
        {
            var key = $"{padding}:{7}";
            await cache.RemoveAsync<string>(key, Ct);
        });

        allocated.Should().BeGreaterThan(12_000);
    }

    private static long Allocated(Func<Task> call)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        call().GetAwaiter().GetResult();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static Task<string?> Generate(int state, CancellationToken token) => Task.FromResult<string?>("v");
}
