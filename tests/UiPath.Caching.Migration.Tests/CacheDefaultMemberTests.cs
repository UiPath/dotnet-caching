using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// The sealed members of ICache<T> are what a service calls most (the span-key forms, the many-key forms); each must reach the router only through an abstract member that has a rule.
public sealed class CacheDefaultMemberTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Func<CancellationToken, Task<string?>> Make => _ => Task.FromResult<string?>(null);

    private static Func<CancellationToken, Task<GeneratedValue<string>>> MakeExpiring => _ => Task.FromResult(new GeneratedValue<string>(null));

    // One call for every sealed member; a new one on the interface has no entry here and fails the first test below.
    private static Dictionary<string, Func<ICache<string>, Task>> Calls { get; } = BuildCalls();

    [Fact]
    public void Every_sealed_member_of_ICache_has_a_recorded_call_here_so_a_new_one_cannot_bypass_the_check()
    {
        var sealedMembers = typeof(ICache<>).GetMethods().Where(m => !m.IsAbstract && !m.IsSpecialName).Select(MemberSignature.Of).ToList();

        sealedMembers.Where(m => !Calls.ContainsKey(m)).Should().BeEmpty("a sealed member added to ICache<T> must be called through the recording cache below");
        Calls.Keys.Where(k => !sealedMembers.Contains(k)).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_sealed_member_reaches_only_abstract_members_that_have_a_router_rule_and_reaches_at_least_one()
    {
        foreach (var (signature, call) in Calls)
        {
            var (cache, log) = RecordingCache.Create();

            await call(cache);

            log.Calls.Should().NotBeEmpty($"{signature} must reach an abstract member, or it is answered without the router");
            log.Calls.Where(c => c != "Name" && !CacheMemberRules.Table.ContainsKey(c)).Should().BeEmpty($"{signature} reached a member with no rule");
        }
    }

    [Fact]
    public void A_routed_cache_does_not_take_the_span_key_path_that_the_sealed_members_would_prefer()
    {
        typeof(RoutedCache<Item>).GetInterfaces().Should().NotContain(typeof(ISpanKeyCache<Item>), "a cache with the span-key capability takes the sealed members past the CacheKey members the router implements");
    }

    private static CacheKeyHandler Key()
    {
        var handler = new CacheKeyHandler(1, 0);
        handler.AppendLiteral("k");
        return handler;
    }

    private static Dictionary<string, Func<ICache<string>, Task>> BuildCalls()
    {
        var item = "v";
        var span = TimeSpan.FromMinutes(1);
        var at = DateTimeOffset.UnixEpoch.AddYears(60);
        var comparer = EqualityComparer<string>.Default;
        static Task<string?> Made(int state, CancellationToken token) => Task.FromResult<string?>(null);
        static Task<GeneratedValue<string>> MadeExpiring(int state, CancellationToken token) => Task.FromResult(new GeneratedValue<string>(null));
        var many = new[] { new KeyValuePair<CacheKey, string?>("a", item) };
        return new Dictionary<string, Func<ICache<string>, Task>>(StringComparer.Ordinal)
        {
            ["ContainsAsync(ref CacheKeyHandler, CancellationToken)"] = c => { var k = Key(); return c.ContainsAsync(ref k, Ct).AsTask(); },
            ["ExpireTimeAsync(ref CacheKeyHandler, CancellationToken)"] = c => { var k = Key(); return c.ExpireTimeAsync(ref k, Ct).AsTask(); },
            ["GetAsync(ref CacheKeyHandler, CancellationToken)"] = c => { var k = Key(); return c.GetAsync(ref k, Ct).AsTask(); },
            ["GetOrAddAsync(ref CacheKeyHandler, Func<CancellationToken,Task<T>>, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddAsync(ref k, Make, Ct).AsTask(); },
            ["GetOrAddAsync(ref CacheKeyHandler, Func<CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddAsync(ref k, Make, at, Ct).AsTask(); },
            ["GetOrAddAsync(ref CacheKeyHandler, Func<CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddAsync(ref k, Make, span, Ct).AsTask(); },
            ["GetOrAddAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<T>>, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddAsync(ref k, 1, Made, Ct).AsTask(); },
            ["GetOrAddAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddAsync(ref k, 1, Made, at, Ct).AsTask(); },
            ["GetOrAddAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddAsync(ref k, 1, Made, span, Ct).AsTask(); },
            ["GetOrAddResultAsync(CacheKey, Func<CancellationToken,Task<T>>, CancellationToken)"] = c => c.GetOrAddResultAsync("k", Make, Ct).AsTask(),
            ["GetOrAddResultAsync(CacheKey, Func<CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = c => c.GetOrAddResultAsync("k", Make, at, Ct).AsTask(),
            ["GetOrAddResultAsync(CacheKey, Func<CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = c => c.GetOrAddResultAsync("k", Make, span, Ct).AsTask(),
            ["GetOrAddResultAsync(ref CacheKeyHandler, Func<CancellationToken,Task<T>>, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddResultAsync(ref k, Make, Ct).AsTask(); },
            ["GetOrAddResultAsync(ref CacheKeyHandler, Func<CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddResultAsync(ref k, Make, at, Ct).AsTask(); },
            ["GetOrAddResultAsync(ref CacheKeyHandler, Func<CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddResultAsync(ref k, Make, span, Ct).AsTask(); },
            ["GetOrAddResultAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<T>>, CancellationToken)"] = c => c.GetOrAddResultAsync("k", 1, Made, Ct).AsTask(),
            ["GetOrAddResultAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = c => c.GetOrAddResultAsync("k", 1, Made, at, Ct).AsTask(),
            ["GetOrAddResultAsync<TState>(CacheKey, TState, Func<TState,CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = c => c.GetOrAddResultAsync("k", 1, Made, span, Ct).AsTask(),
            ["GetOrAddResultAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<T>>, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddResultAsync(ref k, 1, Made, Ct).AsTask(); },
            ["GetOrAddResultAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<T>>, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddResultAsync(ref k, 1, Made, at, Ct).AsTask(); },
            ["GetOrAddResultAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<T>>, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddResultAsync(ref k, 1, Made, span, Ct).AsTask(); },
            ["GetOrAddWithExpirationAsync(ref CacheKeyHandler, Func<CancellationToken,Task<GeneratedValue<T>>>, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddWithExpirationAsync(ref k, MakeExpiring, Ct).AsTask(); },
            ["GetOrAddWithExpirationAsync<TState>(ref CacheKeyHandler, TState, Func<TState,CancellationToken,Task<GeneratedValue<T>>>, CancellationToken)"] = c => { var k = Key(); return c.GetOrAddWithExpirationAsync(ref k, 1, MadeExpiring, Ct).AsTask(); },
            ["RefreshAsync(ref CacheKeyHandler, CancellationToken)"] = c => { var k = Key(); return c.RefreshAsync(ref k, Ct).AsTask(); },
            ["RefreshAsync(ref CacheKeyHandler, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.RefreshAsync(ref k, at, Ct).AsTask(); },
            ["RefreshAsync(ref CacheKeyHandler, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.RefreshAsync(ref k, span, Ct).AsTask(); },
            ["RemoveAsync(ref CacheKeyHandler, CancellationToken)"] = c => { var k = Key(); return c.RemoveAsync(ref k, Ct).AsTask(); },
            ["RemoveManyAsync(IEnumerable<CacheKey>, CancellationToken)"] = c => c.RemoveManyAsync(["a"], Ct).AsTask(),
            ["SetAsync(ref CacheKeyHandler, T, CancellationToken)"] = c => { var k = Key(); return c.SetAsync(ref k, item, Ct).AsTask(); },
            ["SetAsync(ref CacheKeyHandler, T, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.SetAsync(ref k, item, at, Ct).AsTask(); },
            ["SetAsync(ref CacheKeyHandler, T, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.SetAsync(ref k, item, span, Ct).AsTask(); },
            ["SetIfChangedAsync(ref CacheKeyHandler, T, IEqualityComparer<T>, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.SetIfChangedAsync(ref k, item, comparer, at, Ct).AsTask(); },
            ["SetIfChangedAsync(ref CacheKeyHandler, T, IEqualityComparer<T>, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.SetIfChangedAsync(ref k, item, comparer, span, Ct).AsTask(); },
            ["SetManyAsync(IEnumerable<KeyValuePair<CacheKey,T>>, CancellationToken)"] = c => c.SetManyAsync(many, Ct).AsTask(),
            ["SetManyAsync(IEnumerable<KeyValuePair<CacheKey,T>>, DateTimeOffset, CancellationToken)"] = c => c.SetManyAsync(many, at, Ct).AsTask(),
            ["SetManyAsync(IEnumerable<KeyValuePair<CacheKey,T>>, TimeSpan, CancellationToken)"] = c => c.SetManyAsync(many, span, Ct).AsTask(),
            ["TimeToLiveAsync(ref CacheKeyHandler, CancellationToken)"] = c => { var k = Key(); return c.TimeToLiveAsync(ref k, Ct).AsTask(); },
            ["TryAddAsync(ref CacheKeyHandler, T, CancellationToken)"] = c => { var k = Key(); return c.TryAddAsync(ref k, item, Ct).AsTask(); },
            ["TryAddAsync(ref CacheKeyHandler, T, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.TryAddAsync(ref k, item, at, Ct).AsTask(); },
            ["TryAddAsync(ref CacheKeyHandler, T, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.TryAddAsync(ref k, item, span, Ct).AsTask(); },
            ["TryFillAsync(ref CacheKeyHandler, T, DateTimeOffset, CancellationToken)"] = c => { var k = Key(); return c.TryFillAsync(ref k, item, at, Ct).AsTask(); },
            ["TryFillAsync(ref CacheKeyHandler, T, TimeSpan, CancellationToken)"] = c => { var k = Key(); return c.TryFillAsync(ref k, item, span, Ct).AsTask(); },
        };
    }
}
