using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Config;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests;

#pragma warning disable CA1859 // the cache variables are typed as the interface on purpose, to exercise interface binding
#pragma warning disable xUnit1051 // the cases pass the token explicitly where the call takes one

/// <summary>GetOrAddResultAsync says whether this call's generator produced the value, and when the entry expires.</summary>
[Collection(AllocationCollection.Name)]
public class GetOrAddResultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<CancellationToken, Task<string?>> Unexpected { get; } = static _ => throw new InvalidOperationException("the generator ran on a hit");

    private static Func<Owner, int, CancellationToken, Task<string?>> UnexpectedOwned { get; } = static (_, _, _) => throw new InvalidOperationException("the generator ran on a hit");

    [Fact]
    public async Task Miss_reports_generated()
    {
        using var cache = InMemoryMultilayer.Cache();

        var result = await cache.GetOrAddResultAsync<string>("k:miss", _ => Task.FromResult<string?>("v"), policy: null, Ct);

        result.Value.Should().Be("v");
        result.WasGenerated.Should().BeTrue();
    }

    [Fact]
    public async Task Hit_reports_not_generated()
    {
        using var cache = InMemoryMultilayer.Cache();
        await cache.GetOrAddAsync<string>("k:hit", _ => Task.FromResult<string?>("v"), policy: null, Ct);

        var result = await cache.GetOrAddResultAsync<string>("k:hit", Unexpected, policy: null, Ct);

        result.Value.Should().Be("v");
        result.WasGenerated.Should().BeFalse();
    }

    [Fact]
    public async Task Every_form_reports_a_miss_then_a_hit()
    {
        using var cache = InMemoryMultilayer.Cache();
        var owner = new Owner("o");
        var minute = TimeSpan.FromMinutes(1);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);

        (await cache.GetOrAddResultAsync<string>("k:a", _ => Task.FromResult<string?>("1"), minute, null, Ct)).WasGenerated.Should().BeTrue();
        (await cache.GetOrAddResultAsync<string>("k:a", Unexpected, minute, null, Ct)).WasGenerated.Should().BeFalse();
        (await cache.GetOrAddResultAsync<string>("k:b", _ => Task.FromResult<string?>("2"), deadline, null, Ct)).WasGenerated.Should().BeTrue();
        (await cache.GetOrAddResultAsync<string>((CacheKey)"k:b", Unexpected, deadline, null, Ct)).WasGenerated.Should().BeFalse();
        (await cache.GetOrAddResultAsync<string, int>("k:c", 3, (s, _) => Task.FromResult<string?>($"{s}"), policy: null, Ct)).Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "3");
        (await cache.GetOrAddResultAsync<string, int>((CacheKey)"k:c", 4, (_, _) => Task.FromResult<string?>("no"), policy: null, Ct)).Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "3");
        (await cache.GetOrAddResultAsync("k:d", owner, 5, static (o, a, _) => Task.FromResult<string?>($"{o}:{a}"), policy: null, Ct)).Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == $"{owner}:5");
        (await cache.GetOrAddResultAsync("k:d", owner, 6, static (_, _, _) => Task.FromResult<string?>("no"), policy: null, Ct)).Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == $"{owner}:5");
    }

    [Fact]
    public async Task The_typed_cache_reports_a_miss_then_a_hit()
    {
        using var inner = InMemoryMultilayer.Cache();
        ICache<string> typed = new Cache<string>(inner);

        var miss = await typed.GetOrAddResultAsync("k:typed", _ => Task.FromResult<string?>("v"), Ct);
        var hit = await typed.GetOrAddResultAsync("k:typed", Unexpected, Ct);
        var handlerHit = await GetOrAddResultByHandlerAsync(typed);

        miss.WasGenerated.Should().BeTrue();
        hit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "v");
        handlerHit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "v");
    }

    [Fact]
    public async Task Waiting_on_a_concurrent_miss_reports_not_generated()
    {
        using var cache = InMemoryMultilayer.Cache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = cache.GetOrAddResultAsync<string>(
            "k:shared",
            async _ =>
            {
                started.SetResult();
                return await release.Task;
            },
            policy: null,
            Ct).AsTask();
        await started.Task;
        var second = cache.GetOrAddResultAsync<string>("k:shared", Unexpected, policy: null, Ct).AsTask();
        await Task.Delay(50, Ct);
        second.IsCompleted.Should().BeFalse("it waits on the run in progress");
        release.SetResult("v");

        var results = await Task.WhenAll(first, second);

        results[0].Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "v");
        results[1].Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "v");
    }

    [Fact]
    public async Task Expiration_is_the_stored_expiration()
    {
        using var cache = InMemoryMultilayer.Cache();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(7);

        var miss = await cache.GetOrAddResultAsync<string>("k:exp", _ => Task.FromResult<string?>("v"), deadline, null, Ct);
        var hit = await cache.GetOrAddResultAsync<string>("k:exp", Unexpected, deadline.AddHours(1), null, Ct);

        miss.Expiration.Should().Be(deadline);
        hit.Expiration.Should().Be(deadline, "a hit reports the entry's own expiration");
    }

    [Fact]
    public async Task Default_implementation_reports_correctly()
    {
        ICache cache = new GeneratingCache();
        var calls = 0;

        var first = await cache.GetOrAddResultAsync<string>("k:default", _ => { calls++; return Task.FromResult<string?>("v"); }, policy: null, Ct);
        var second = await cache.GetOrAddResultAsync<string>("k:default", Unexpected, policy: null, Ct);
        var byState = await cache.GetOrAddResultAsync<string, int>("k:default", 1, (_, _) => Task.FromResult<string?>("no"), policy: null, Ct);
        var byHandler = await GetOrAddResultByHandlerAsync(cache);

        first.Should().Be(new CacheResult<string>("v", null, WasGenerated: true));
        second.Should().Be(new CacheResult<string>("v", null, WasGenerated: false));
        byState.WasGenerated.Should().BeFalse();
        byHandler.WasGenerated.Should().BeFalse();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Default_implementation_reports_a_caller_deadline_when_it_generated()
    {
        ICache cache = new GeneratingCache();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);

        var miss = await cache.GetOrAddResultAsync<string>("k:deadline", _ => Task.FromResult<string?>("v"), deadline, null, Ct);
        var hit = await cache.GetOrAddResultAsync<string>("k:deadline", Unexpected, deadline, null, Ct);

        miss.Expiration.Should().Be(deadline);
        hit.Expiration.Should().BeNull("a hit's expiration is unknown to the default implementation");
    }

    [Fact]
    public async Task A_failed_generation_leaves_the_retry_to_generate()
    {
        using var cache = InMemoryMultilayer.Cache();

        await FluentActions.Awaiting(async () => await cache.GetOrAddResultAsync<string>("k:fail", _ => throw new InvalidOperationException("boom"), policy: null, Ct)).Should().ThrowAsync<InvalidOperationException>();
        var retry = await cache.GetOrAddResultAsync<string>("k:fail", _ => Task.FromResult<string?>("v"), policy: null, Ct);

        retry.Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "v");
    }

    [Fact]
    public async Task A_cancelled_call_leaves_the_retry_to_generate()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions.Awaiting(async () => await cache.GetOrAddResultAsync<string>("k:cancel", _ => Task.FromResult<string?>("no"), policy: null, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        var retry = await cache.GetOrAddResultAsync<string>("k:cancel", _ => Task.FromResult<string?>("v"), policy: null, Ct);

        retry.Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "v");
    }

    [Fact]
    public async Task A_generated_null_that_is_not_cached_reports_no_expiration()
    {
        using var cache = InMemoryMultilayer.Cache();

        var result = await cache.GetOrAddResultAsync<string>("k:null-uncached", _ => Task.FromResult<string?>(null), TimeSpan.FromMinutes(5), null, Ct);

        result.Should().Be(new CacheResult<string>(null, null, WasGenerated: true));
    }

    [Fact]
    public async Task A_generated_null_that_is_cached_reports_its_expiration()
    {
        using var cache = InMemoryMultilayer.Cache(new InMemoryCacheOptions { CacheNullValues = true });

        var result = await cache.GetOrAddResultAsync<string>("k:null-cached", _ => Task.FromResult<string?>(null), TimeSpan.FromMinutes(5), null, Ct);

        result.WasGenerated.Should().BeTrue();
        result.Expiration.Should().NotBeNull();
    }

    [Fact]
    public async Task The_null_cache_takes_an_interpolated_key()
    {
        var cache = NullCache.Instance;
        var text = "k:null-handler";

        var byClosure = await cache.GetOrAddResultAsync<string>($"{text}", _ => Task.FromResult<string?>("v"), (CachePolicy?)null, Ct);
        var byState = await cache.GetOrAddResultAsync<string, int>($"{text}", 1, (_, _) => Task.FromResult<string?>("w"), TimeSpan.FromMinutes(1), null, Ct);
        var byDeadline = await cache.GetOrAddResultAsync<string>($"{text}", _ => Task.FromResult<string?>("x"), DateTimeOffset.UtcNow.AddMinutes(1), null, Ct);

        byClosure.Should().Be(new CacheResult<string>("v", null, WasGenerated: true));
        byState.Should().Be(new CacheResult<string>("w", null, WasGenerated: true));
        byDeadline.Should().Be(new CacheResult<string>("x", null, WasGenerated: true));
    }

    [Fact]
    public async Task The_null_cache_always_generates()
    {
        ICache cache = NullCache.Instance;

        var first = await cache.GetOrAddResultAsync<string>("k:null", _ => Task.FromResult<string?>("v"), policy: null, Ct);
        var second = await cache.GetOrAddResultAsync<string, int>("k:null", 1, (_, _) => Task.FromResult<string?>("w"), policy: null, Ct);

        first.Should().Be(new CacheResult<string>("v", null, WasGenerated: true));
        second.Should().Be(new CacheResult<string>("w", null, WasGenerated: true));
    }

    [Fact]
    public async Task A_null_generator_is_refused()
    {
        using var cache = InMemoryMultilayer.Cache();
        ICache custom = new GeneratingCache();

        await FluentActions.Awaiting(async () => await cache.GetOrAddResultAsync<string>("k:n", (Func<CancellationToken, Task<string?>>)null!, policy: null, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await custom.GetOrAddResultAsync<string>("k:n", (Func<CancellationToken, Task<string?>>)null!, policy: null, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(async () => await cache.GetOrAddResultAsync("k:n", new Owner("o"), 1, (Func<Owner, int, CancellationToken, Task<string?>>)null!, policy: null, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task Hit_allocates_nothing()
    {
        using var multilayer = InMemoryMultilayer.Cache();
        ICache cache = multilayer;
        var owner = new Owner("o");
        CacheKey key = "k:alloc";
        await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>("v"), policy: null, Ct);

        AllocatedBy(() => Complete(cache.GetOrAddResultAsync<string>(key, Unexpected, policy: null, Ct))).Should().Be(0, "the CacheKey form");
        AllocatedBy(() => Complete(cache.GetOrAddResultAsync<string>(key, Unexpected, TimeSpan.FromMinutes(1), null, Ct))).Should().Be(0, "the TimeSpan form");
        AllocatedBy(() => Complete(cache.GetOrAddResultAsync(key, owner, 1, UnexpectedOwned, policy: null, Ct))).Should().Be(0, "the owner and argument form");
        AllocatedBy(() => Complete(cache.GetOrAddResultAsync<string>(key, Unexpected, DateTimeOffset.UtcNow.AddMinutes(1), null, Ct))).Should().Be(0, "the DateTimeOffset form");
        AllocatedBy(() => HandlerHit(cache)).Should().Be(0, "the handler-key form");
    }

    [Fact]
    public async Task A_typed_cache_hit_allocates_nothing()
    {
        using var multilayer = InMemoryMultilayer.Cache();
        ICache<string> typed = new Cache<string>(multilayer);
        CacheKey key = "k:typed-alloc";
        await typed.GetOrAddResultAsync(key, _ => Task.FromResult<string?>("v"), Ct);

        AllocatedBy(() => Complete(typed.GetOrAddResultAsync(key, Unexpected, Ct))).Should().Be(0, "the composed CacheKey form");
        AllocatedBy(() => Complete(typed.GetOrAddResultAsync(key, Unexpected, TimeSpan.FromMinutes(1), Ct))).Should().Be(0, "the TimeSpan form");
        AllocatedBy(() => TypedHandlerHit(typed)).Should().Be(0, "the handler-key form");
    }

    private static CacheResult<string> TypedHandlerHit(ICache<string> cache)
    {
        var text = "k:typed-alloc";
        return Complete(cache.GetOrAddResultAsync($"{text}", Unexpected, Ct));
    }

    private static CacheResult<string> HandlerHit(ICache cache)
    {
        var text = "k:alloc";
        return Complete(cache.GetOrAddResultAsync<string>($"{text}", Unexpected, (CachePolicy?)null, Ct));
    }

    private static long AllocatedBy(Action read)
    {
        var fewest = long.MaxValue;
        for (var round = 0; round < 10 && fewest > 0; round++)
        {
            for (var i = 0; i < 1_000; i++)
            {
                read();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1_000; i++)
            {
                read();
            }

            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }
#endif

    private static ValueTask<CacheResult<string>> GetOrAddResultByHandlerAsync(ICache cache)
    {
        var text = "k:default";
        return cache.GetOrAddResultAsync<string>($"{text}", Unexpected, (CachePolicy?)null, Ct);
    }

    private static ValueTask<CacheResult<string>> GetOrAddResultByHandlerAsync(ICache<string> cache)
    {
        var text = "k:typed";
        return cache.GetOrAddResultAsync($"{text}", Unexpected, Ct);
    }

#pragma warning disable S5034 // the hit completes synchronously; the wait only covers a miss
    private static TResult Complete<TResult>(ValueTask<TResult> read) => read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
#pragma warning restore S5034

    /// <summary>An <see cref="ICache"/> that stores one value per key and has no GetOrAddResultAsync of its own, as a cache outside the library has none.</summary>
    private sealed record Owner(string Name);

    private sealed class GeneratingCache : DictionaryCache, ICache
    {
        ValueTask<T?> ICache.GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CachePolicy? policy, CancellationToken token)
            where T : default =>
            Read<T>(cacheKey) is { } cached ? new ValueTask<T?>(cached) : Store(cacheKey, generator, token);

        ValueTask<T?> ICache.GetOrAddAsync<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, DateTimeOffset expiration, CachePolicy? policy, CancellationToken token)
            where T : default =>
            Read<T>(cacheKey) is { } cached ? new ValueTask<T?>(cached) : Store(cacheKey, generator, token);

        private async ValueTask<T?> Store<T>(CacheKey cacheKey, Func<CancellationToken, Task<T?>> generator, CancellationToken token)
        {
            var value = await generator(token);
            Seed(cacheKey, value);
            return value;
        }
    }
}

/// <summary>The same reports from the Redis-backed caches.</summary>
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class GetOrAddResultRedisIntegrationTests(RedisContainerFixture fixture) : IDisposable
{
    private readonly string _app = $"gr{Guid.NewGuid():N}"[..10];
    private readonly List<ServiceProvider> _nodes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(KnownCacheProviderNames.Redis)]
    [InlineData(KnownCacheProviderNames.InMemoryRedis)]
    public async Task A_miss_reports_generated_and_a_hit_does_not(string provider)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(provider);
        CacheKey key = $"k-{Guid.NewGuid():N}";

        var miss = await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>("v"), TimeSpan.FromMinutes(5), null, Ct);
        var hit = await cache.GetOrAddResultAsync<string>(key, _ => throw new InvalidOperationException("ran on a hit"), TimeSpan.FromMinutes(5), null, Ct);

        miss.Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "v");
        hit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "v");
        miss.Expiration.Should().BeAfter(DateTimeOffset.UtcNow);
        hit.Expiration.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_redis_hit_allocates_about_what_get_or_add_does()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis);
        CacheKey key = $"k-{Guid.NewGuid():N}";
        await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>("v"), TimeSpan.FromMinutes(5), null, Ct);
        static Task<string?> Fail(CancellationToken token) => throw new InvalidOperationException("ran on a hit");

        var plain = await AllocatedPerCallAsync(() => cache.GetOrAddAsync(key, Fail, TimeSpan.FromMinutes(5), null, Ct).AsTask());
        var result = await AllocatedPerCallAsync(() => cache.GetOrAddResultAsync(key, Fail, TimeSpan.FromMinutes(5), null, Ct).AsTask());

        result.Should().BeLessThan((plain * 2) + 1024, $"a plain hit allocated {plain} B and a result hit {result} B");
    }

    [Fact]
    public async Task Without_the_read_script_a_hit_reports_the_stored_deadline()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis, readScripts: false);
        CacheKey key = $"k-{Guid.NewGuid():N}";
        var before = DateTimeOffset.UtcNow;
        await cache.SetAsync(key, "stored", TimeSpan.FromMinutes(5), null, Ct);

        var hit = await cache.GetOrAddResultAsync<string>(key, _ => throw new InvalidOperationException("ran on a hit"), TimeSpan.FromMinutes(9), null, Ct);

        hit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "stored");
        hit.Expiration.Should().BeCloseTo(before.AddMinutes(5), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Without_the_read_script_a_hit_with_no_expiry_reports_none()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis, readScripts: false);
        CacheKey key = $"k-{Guid.NewGuid():N}";
        await cache.SetAsync(key, "forever", TimeSpan.MaxValue, null, Ct);

        var hit = await cache.GetOrAddResultAsync<string>(key, _ => throw new InvalidOperationException("ran on a hit"), TimeSpan.FromMinutes(9), null, Ct);

        hit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "forever" && r.Expiration == null);
    }

    [Fact]
    public async Task Without_the_read_script_a_missing_key_generates_and_reports_the_deadline()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis, readScripts: false);
        CacheKey key = $"k-{Guid.NewGuid():N}";
        var before = DateTimeOffset.UtcNow;

        var miss = await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>("made"), TimeSpan.FromMinutes(5), null, Ct);
        var hit = await cache.GetOrAddResultAsync<string>(key, _ => throw new InvalidOperationException("ran on a hit"), TimeSpan.FromMinutes(5), null, Ct);

        miss.Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "made");
        miss.Expiration.Should().BeCloseTo(before.AddMinutes(5), TimeSpan.FromSeconds(2));
        hit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == "made");
        hit.Expiration.Should().BeCloseTo(before.AddMinutes(5), TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cached_null_is_a_hit_with_or_without_the_read_script(bool readScripts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis, readScripts, cacheNulls: true);
        CacheKey key = $"k-{Guid.NewGuid():N}";

        var miss = await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>(null), TimeSpan.FromMinutes(5), null, Ct);
        var hit = await cache.GetOrAddResultAsync<string>(key, _ => throw new InvalidOperationException("ran on a hit"), TimeSpan.FromMinutes(5), null, Ct);

        miss.Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == null);
        hit.Should().Match<CacheResult<string>>(r => !r.WasGenerated && r.Value == null);
        hit.Expiration.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_plain_call_and_a_result_call_on_one_cold_key_share_one_run(bool resultCallStarts)
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis);
        CacheKey key = $"k-{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        async Task<string?> Gated(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            return await release.Task;
        }

        Task<string?> Joiner(CancellationToken token)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult<string?>("second");
        }

        Task<CacheResult<string>> first;
        Task<CacheResult<string>> second;
        if (resultCallStarts)
        {
            first = cache.GetOrAddResultAsync<string>(key, Gated, TimeSpan.FromMinutes(5), null, Ct).AsTask();
            await started.Task;
            second = Wrap(cache.GetOrAddAsync<string>(key, Joiner, TimeSpan.FromMinutes(5), null, Ct).AsTask());
        }
        else
        {
            first = Wrap(cache.GetOrAddAsync<string>(key, Gated, TimeSpan.FromMinutes(5), null, Ct).AsTask());
            await started.Task;
            second = cache.GetOrAddResultAsync<string>(key, Joiner, TimeSpan.FromMinutes(5), null, Ct).AsTask();
        }

        await Task.Delay(100, Ct);
        second.IsCompleted.Should().BeFalse("it joins the run in progress");
        release.SetResult("first");
        var results = await Task.WhenAll(first, second);

        runs.Should().Be(1);
        results.Select(r => r.Value).Should().AllBe("first");
        (resultCallStarts ? results[0] : results[1]).WasGenerated.Should().Be(resultCallStarts);
        results.Should().OnlyContain(r => r.Expiration == null || r.Expiration > DateTimeOffset.UtcNow);
        if (!resultCallStarts)
        {
            results[1].Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(10), "it joined a plain run on a miss and reports what that run stored");
        }
    }

    [Fact]
    public async Task A_plain_call_still_blocks_on_an_unfinished_run_like_before()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis);
        CacheKey key = $"k-{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ReleaseLaterAsync()
        {
            await started.Task;
            await Task.Delay(200, Ct);
            release.SetResult("v");
        }

        async Task<string?> Gated(CancellationToken token)
        {
            started.SetResult();
            return await release.Task;
        }

#pragma warning disable CA2012 // the ValueTask is kept so the test can block on it unfinished
        var pending = cache.GetOrAddAsync<string>(key, Gated, TimeSpan.FromMinutes(5), null, Ct);
        await started.Task;
        pending.IsCompleted.Should().BeFalse();
        _ = ReleaseLaterAsync();

#pragma warning disable xUnit1031, S5034 // the point is the blocking wait on a ValueTask that is not finished
        pending.GetAwaiter().GetResult().Should().Be("v");
#pragma warning restore xUnit1031, S5034, CA2012
    }

    [Fact]
    public async Task A_finite_expiration_beyond_the_calendar_does_not_fail_the_call()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis);
        CacheKey key = $"k-{Guid.NewGuid():N}";

        var miss = await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>("v"), TimeSpan.FromDays(3_000_000), null, Ct);

        miss.Should().Match<CacheResult<string>>(r => r.WasGenerated && r.Value == "v");
    }

    [Fact]
    public async Task A_generated_null_that_is_not_cached_reports_no_expiration_on_redis()
    {
        Assert.SkipUnless(fixture.Enabled, "Set RUN_REDIS_INTEGRATION_TESTS=1 (Docker required) to run.");
        var cache = Cache(KnownCacheProviderNames.Redis);
        CacheKey key = $"k-{Guid.NewGuid():N}";

        var result = await cache.GetOrAddResultAsync<string>(key, _ => Task.FromResult<string?>(null), TimeSpan.FromMinutes(5), null, Ct);

        result.Should().Be(new CacheResult<string>(null, null, WasGenerated: true));
    }

    public void Dispose()
    {
        foreach (var node in _nodes)
        {
            node.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static async Task<CacheResult<string>> Wrap(Task<string?> plain) => new(await plain, null, WasGenerated: false);

    private static async Task<long> AllocatedPerCallAsync(Func<Task> call)
    {
        for (var i = 0; i < 50; i++)
        {
            await call();
        }

        // The fewest of several rounds: other tests allocate on their own threads meanwhile, and a call's continuations run on pool threads.
        var fewest = long.MaxValue;
        for (var round = 0; round < 7; round++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < 200; i++)
            {
                await call();
            }

            fewest = Math.Min(fewest, (GC.GetTotalAllocatedBytes(precise: true) - before) / 200);
        }

        return fewest;
    }

    private ICache Cache(string provider, bool readScripts = true, bool cacheNulls = false)
    {
        var node = new ServiceCollection()
            .AddLogging()
            .AddCaching(
                b =>
                {
                    b.AddRedisConnection(o => o.ConnectionString = fixture.ConnectionString);
                    b.AddRedis(o =>
                    {
                        o.ReadScriptsEnabled = readScripts;
                        o.CacheNullValues = cacheNulls;
                    });
                    b.AddBroadcast();
                    b.AddInMemoryRedis(o => o.LocalMaxExpiration = TimeSpan.FromMinutes(1));
                },
                o =>
                {
                    o.AppShortName = _app;
                    o.SourceUri = new Uri($"urn:{_app}-{_nodes.Count}");
                })
            .BuildServiceProvider();
        _nodes.Add(node);
        return node.GetRequiredService<ICacheFactory>().CreateCache(provider);
    }
}
