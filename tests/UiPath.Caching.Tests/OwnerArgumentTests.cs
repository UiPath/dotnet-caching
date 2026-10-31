using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

#pragma warning disable CA1859 // the cache variables are typed as the interface on purpose, to exercise interface binding
#pragma warning disable xUnit1051 // the cases pass the token explicitly where the call takes one

/// <summary>The owner and argument overloads hand a static generator both values and behave like the state members they forward to.</summary>
[Collection(AllocationCollection.Name)]
public class OwnerArgumentTests
{
    private static readonly DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddMinutes(7);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<Owner, int, CancellationToken, Task<string?>> UnexpectedValue { get; } = static (_, _, _) => throw new InvalidOperationException("the generator ran on a hit");

    private static Func<Owner, int, CancellationToken, Task<IDictionary<string, string?>>> UnexpectedHash { get; } = static (_, _, _) => throw new InvalidOperationException("the generator ran on a hit");

    private static Func<Owner, int, CancellationToken, Task<GeneratedValue<string>>> UnexpectedExpiring { get; } = static (_, _, _) => throw new InvalidOperationException("the generator ran on a hit");

    [Fact]
    public async Task Owner_and_argument_reach_the_generator()
    {
        using var cache = InMemoryMultilayer.Cache();
        var owner = new Owner("o");
        Owner? seenOwner = null;

        var value = await cache.GetOrAddAsync($"k:{1}", owner, 5, (o, a, t) => { seenOwner = o; return Compose(o, a, t); }, policy: null, Ct);
        var timed = await cache.GetOrAddAsync($"k:span", owner, 6, Compose, TimeSpan.FromMinutes(5), null, Ct);
        var dated = await cache.GetOrAddAsync($"k:when", owner, 7, Compose, Deadline, null, Ct);
        var keyed = await cache.GetOrAddAsync((CacheKey)"k:key", owner, 8, Compose, policy: null, Ct);

        value.Should().Be("o:5");
        seenOwner.Should().BeSameAs(owner);
        timed.Should().Be("o:6");
        dated.Should().Be("o:7");
        keyed.Should().Be("o:8");
    }

    [Fact]
    public async Task A_hit_does_not_run_the_generator()
    {
        using var cache = InMemoryMultilayer.Cache();
        var owner = new Owner("o");
        var calls = 0;
        Task<string?> Counting(Owner o, int a, CancellationToken t)
        {
            calls++;
            return Compose(o, a, t);
        }


        await cache.GetOrAddAsync($"k:{1}", owner, 5, Counting, policy: null, Ct);
        (await cache.GetOrAddAsync($"k:{1}", owner, 9, Counting, policy: null, Ct)).Should().Be("o:5");

        calls.Should().Be(1);
    }

#if NET9_0_OR_GREATER
    [Fact]
    public async Task A_warm_hit_allocates_nothing_for_every_untyped_form()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var hashCache = InMemoryMultilayer.HashCache();
        var owner = new Owner("o");
        CacheKey key = "k";
        CacheKey generatedKey = "g";
        await cache.GetOrAddAsync(key, owner, 1, Compose, policy: null, Ct);
        await cache.GetOrAddAsync(key, owner, 1, Compose, TimeSpan.FromMinutes(1), null, Ct);
        await cache.GetOrAddWithExpirationAsync(generatedKey, owner, 1, ComposeExpiring, policy: null, Ct);
        await hashCache.GetOrAddAsync<string, Owner, int>(key, owner, 1, ComposeHash, policy: null, Ct);

        AllocatedBy(() => Complete(cache.GetOrAddAsync(key, owner, 1, UnexpectedValue, policy: null, Ct))).Should().Be(0, "the CacheKey form");
        AllocatedBy(() => Complete(cache.GetOrAddAsync(key, owner, 1, UnexpectedValue, TimeSpan.FromMinutes(1), null, Ct))).Should().Be(0, "the TimeSpan form");
        AllocatedBy(() => HandlerHit(cache, owner)).Should().Be(0, "the handler-key form");
        AllocatedBy(() => Complete(hashCache.GetOrAddAsync<string, Owner, int>(key, owner, 1, UnexpectedHash, policy: null, Ct))).Should().Be(0, "the hash form");
        AllocatedBy(() => HandlerHashHit(hashCache, owner)).Should().Be(0, "the hash handler-key form");
        AllocatedBy(() => Complete(cache.GetOrAddWithExpirationAsync(generatedKey, owner, 1, UnexpectedExpiring, policy: null, Ct))).Should().Be(0, "the GetOrAddWithExpiration form");
    }

    [Fact]
    public async Task A_warm_hit_allocates_nothing_for_the_typed_forms()
    {
        using var cache = InMemoryMultilayer.Cache();
        using var hashCache = InMemoryMultilayer.HashCache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var typedHash = new HashCache<string>(hashCache, new PrefixCacheKeyStrategy("app"));
        var owner = new Owner("o");
        CacheKey key = "k";
        await typed.GetOrAddAsync(key, owner, 1, Compose, Ct);
        await typedHash.GetOrAddAsync(key, owner, 1, ComposeHash, Ct);

        AllocatedBy(() => Complete(typed.GetOrAddAsync(key, owner, 1, UnexpectedValue, Ct))).Should().Be(0, "the typed CacheKey form");
        AllocatedBy(() => TypedHandlerHit(typed, owner)).Should().Be(0, "the typed handler-key form");
        AllocatedBy(() => Complete(typedHash.GetOrAddAsync(key, owner, 1, UnexpectedHash, Ct))).Should().Be(0, "the typed hash CacheKey form");
        AllocatedBy(() => TypedHashHandlerHit(typedHash, owner)).Should().Be(0, "the typed hash handler-key form");
    }
#endif

    [Fact]
    public async Task Generator_exception_surfaces_and_nothing_is_cached()
    {
        using var cache = InMemoryMultilayer.Cache();
        var owner = new Owner("o");

        var thrown = async () => await cache.GetOrAddAsync<string, Owner, int>($"k:{1}", owner, 5, static (_, _, _) => throw new InvalidOperationException("boom"), policy: null, Ct);

        await thrown.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        (await cache.ContainsAsync<string>("k:1", Ct)).Should().BeFalse();
        (await cache.GetOrAddAsync($"k:{1}", owner, 5, Compose, policy: null, Ct)).Should().Be("o:5");
    }

    [Fact]
    public async Task Cancelled_call_cancels_like_the_state_form_and_caches_nothing()
    {
        using var cache = InMemoryMultilayer.Cache();
        var owner = new Owner("o");
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var viaOwner = await Outcome(() => cache.GetOrAddAsync($"k:{1}", owner, 5, Compose, policy: null, source.Token).AsTask());
        var viaState = await Outcome(() => cache.GetOrAddAsync($"k:{2}", owner, static (o, t) => Compose(o, 5, t), policy: null, source.Token).AsTask());

        viaState.Should().Be("canceled");
        viaOwner.Should().Be(viaState);
        (await cache.ContainsAsync<string>("k:1", Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_null_generator_throws_before_forwarding_on_every_cache_interface()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var hash = Substitute.For<IHashCache>();
        var typedHash = Substitute.For<IHashCache<string>>();
        var owner = new Owner("o");
        var value = (Func<Owner, int, CancellationToken, Task<string?>>)null!;
        var generated = (Func<Owner, int, CancellationToken, Task<GeneratedValue<string>>>)null!;
        var dictionary = (Func<Owner, int, CancellationToken, Task<IDictionary<string, string?>>>)null!;

        Func<Task>[] calls =
        [
            () => cache.GetOrAddAsync((CacheKey)"k", owner, 5, value, policy: null, Ct).AsTask(),
            () => cache.GetOrAddAsync($"k:{1}", owner, 5, value, policy: null, Ct).AsTask(),
            () => cache.GetOrAddWithExpirationAsync($"k:{1}", owner, 5, generated, policy: null, Ct).AsTask(),
            () => typed.GetOrAddAsync((CacheKey)"k", owner, 5, value, Ct).AsTask(),
            () => typed.GetOrAddAsync($"k:{1}", owner, 5, value, Ct).AsTask(),
            () => typed.GetOrAddWithExpirationAsync($"k:{1}", owner, 5, generated, Ct).AsTask(),
            () => hash.GetOrAddAsync<string, Owner, int>((CacheKey)"k", owner, 5, dictionary, policy: null, Ct).AsTask(),
            () => hash.GetOrAddAsync<string, Owner, int>($"k:{1}", owner, 5, dictionary, policy: null, Ct).AsTask(),
            () => hash.GetOrAddAsync<string, Owner, int>($"k:{1}", owner, 5, dictionary, Deadline, HashCacheSetOption.KeyReplace, null, Ct).AsTask(),
            () => typedHash.GetOrAddAsync((CacheKey)"k", owner, 5, dictionary, Ct).AsTask(),
            () => typedHash.GetOrAddAsync($"k:{1}", owner, 5, dictionary, Ct).AsTask(),
        ];

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<ArgumentNullException>();
        }

        cache.ReceivedCalls().Should().BeEmpty();
        typed.ReceivedCalls().Should().BeEmpty();
        hash.ReceivedCalls().Should().BeEmpty();
        typedHash.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Hash_variants_pass_owner_argument_and_expiration()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var owner = new Owner("o");

        var plain = await cache.GetOrAddAsync<string, Owner, int>($"h:{1}", owner, 1, ComposeHash, policy: null, Ct);
        var timed = await cache.GetOrAddAsync<string, Owner, int>($"h:span", owner, 2, ComposeHash, TimeSpan.FromMinutes(30), null, Ct);
        var dated = await cache.GetOrAddAsync<string, Owner, int>($"h:when", owner, 3, ComposeHash, Deadline, null, Ct);
        var option = await cache.GetOrAddAsync<string, Owner, int>($"h:set", owner, 4, ComposeHash, Deadline, HashCacheSetOption.KeyReplace, null, Ct);
        var keyed = await cache.GetOrAddAsync<string, Owner, int>((CacheKey)"h:key", owner, 5, ComposeHash, TimeSpan.FromMinutes(30), null, Ct);

        plain["id"].Should().Be("o:1");
        timed["id"].Should().Be("o:2");
        dated["id"].Should().Be("o:3");
        option["id"].Should().Be("o:4");
        keyed["id"].Should().Be("o:5");
        (await cache.GetCacheEntryAsync<string>("h:span", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(30));
        (await cache.GetCacheEntryAsync<string>("h:when", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
        (await cache.GetCacheEntryAsync<string>("h:set", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
        (await cache.GetCacheEntryAsync<string>("h:key", policy: null, Ct)).Expiration.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task The_handler_hash_variant_forwards_the_expiration_and_set_option()
    {
        var hash = Substitute.For<IHashCache>();
        var owner = new Owner("o");

        await hash.GetOrAddAsync<string, Owner, int>($"h:{1}", owner, 1, ComposeHash, Deadline, HashCacheSetOption.KeyReplace, null, Ct);

        var arguments = hash.ReceivedCalls().Single(c => c.GetMethodInfo().Name == "GetOrAddAsync").GetArguments();
        arguments.Should().Contain(Deadline).And.Contain(HashCacheSetOption.KeyReplace);
    }

    [Fact]
    public async Task GetOrAddWithExpiration_variants_pass_owner_argument_and_the_generated_expiration()
    {
        using var cache = InMemoryMultilayer.Cache();
        var owner = new Owner("o");

        (await cache.GetOrAddWithExpirationAsync($"e:{1}", owner, 1, ComposeExpiring, policy: null, Ct)).Should().Be("o:1");
        (await cache.GetOrAddWithExpirationAsync((CacheKey)"e:key", owner, 2, ComposeExpiring, policy: null, Ct)).Should().Be("o:2");
        (await cache.GetOrAddWithExpirationAsync($"e:{1}", owner, 3, UnexpectedExpiring, policy: null, Ct)).Should().Be("o:1");

        (await cache.GetCacheEntryAsync<string>("e:1", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
        (await cache.GetCacheEntryAsync<string>("e:key", policy: null, Ct)).Expiration.Should().BeCloseTo(Deadline, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_typed_cache_runs_the_generator_once_under_its_own_key_strategy()
    {
        using var cache = InMemoryMultilayer.Cache();
        var typed = new Cache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var owner = new Owner("o");

        (await typed.GetOrAddAsync("k:plain", owner, 1, Compose, Ct)).Should().Be("o:1");
        (await typed.GetOrAddAsync("k:plain", owner, 2, UnexpectedValue, Ct)).Should().Be("o:1");
        (await typed.GetOrAddAsync($"k:{3}", owner, 3, Compose, TimeSpan.FromMinutes(5), Ct)).Should().Be("o:3");
        (await typed.GetOrAddAsync($"k:{4}", owner, 4, Compose, Deadline, Ct)).Should().Be("o:4");
        (await typed.GetOrAddWithExpirationAsync($"k:{5}", owner, 5, ComposeExpiring, Ct)).Should().Be("o:5");
        (await typed.GetOrAddWithExpirationAsync($"k:{5}", owner, 6, UnexpectedExpiring, Ct)).Should().Be("o:5");

        (await typed.GetAsync("k:plain", Ct)).Should().Be("o:1");
        (await cache.GetAsync<string>("k:plain", policy: null, Ct)).Should().BeNull("the typed cache stores under its own strategy's key");
    }

    [Fact]
    public async Task A_typed_hash_cache_runs_the_generator_once_under_its_own_key_strategy()
    {
        using var cache = InMemoryMultilayer.HashCache();
        var typed = new HashCache<string>(cache, new PrefixCacheKeyStrategy("app"));
        var owner = new Owner("o");

        (await typed.GetOrAddAsync("h:plain", owner, 1, ComposeHash, Ct))["id"].Should().Be("o:1");
        (await typed.GetOrAddAsync("h:plain", owner, 2, UnexpectedHash, Ct))["id"].Should().Be("o:1");
        (await typed.GetOrAddAsync($"h:{3}", owner, 3, ComposeHash, TimeSpan.FromMinutes(30), Ct))["id"].Should().Be("o:3");
        (await typed.GetOrAddAsync($"h:{4}", owner, 4, ComposeHash, Deadline, Ct))["id"].Should().Be("o:4");

        (await typed.GetAsync("h:plain", Ct))["id"].Should().Be("o:1");
        (await cache.GetAsync<string>("h:plain", policy: null, Ct)).Should().BeEmpty("the typed cache stores under its own strategy's key");
    }

    private static Task<string?> Compose(Owner owner, int argument, CancellationToken token) => Task.FromResult<string?>($"{owner.Name}:{argument}");

    private static Task<IDictionary<string, string?>> ComposeHash(Owner owner, int argument, CancellationToken token) =>
        Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?> { ["id"] = $"{owner.Name}:{argument}" });

    private static Task<GeneratedValue<string>> ComposeExpiring(Owner owner, int argument, CancellationToken token) =>
        Task.FromResult(new GeneratedValue<string>($"{owner.Name}:{argument}", Deadline));

#if NET9_0_OR_GREATER
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

    private static string? HandlerHit(ICache cache, Owner owner)
    {
        var text = "k";
        return Complete(cache.GetOrAddAsync($"{text}", owner, 1, UnexpectedValue, (CachePolicy?)null, Ct));
    }

    private static IDictionary<string, string?> HandlerHashHit(IHashCache cache, Owner owner)
    {
        var text = "k";
        return Complete(cache.GetOrAddAsync<string, Owner, int>($"{text}", owner, 1, UnexpectedHash, (CachePolicy?)null, Ct));
    }

    private static string? TypedHandlerHit(ICache<string> cache, Owner owner)
    {
        var text = "k";
        return Complete(cache.GetOrAddAsync($"{text}", owner, 1, UnexpectedValue, Ct));
    }

    private static IDictionary<string, string?> TypedHashHandlerHit(IHashCache<string> cache, Owner owner)
    {
        var text = "k";
        return Complete(cache.GetOrAddAsync($"{text}", owner, 1, UnexpectedHash, Ct));
    }
#endif

#pragma warning disable S5034 // read once: the result of a completed read, or the awaited one; AsTask on every read would allocate
    private static TResult Complete<TResult>(ValueTask<TResult> read) => read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
#pragma warning restore S5034

    private static async Task<string> Outcome(Func<Task<string?>> call)
    {
        try
        {
            return await call() ?? "null";
        }
        catch (OperationCanceledException)
        {
            return "canceled";
        }
        catch (Exception exception)
        {
            return exception.GetType().Name;
        }
    }

    private sealed record Owner(string Name);
}
