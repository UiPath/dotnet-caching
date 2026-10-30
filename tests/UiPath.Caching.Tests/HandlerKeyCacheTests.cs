using System.Reflection;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

#pragma warning disable CA1859 // the cache variables are typed as the interface on purpose, to exercise interface binding
#pragma warning disable IDE0034 // default(CacheKey) is written out to pin the CacheKey overload; a bare default would bind differently
#pragma warning disable xUnit1051 // the cases pass the token explicitly where the call takes one

/// <summary>Every <c>ICache</c> and <c>ICache&lt;T&gt;</c> member that takes an interpolated key reaches the same member a <c>CacheKey</c> call does, or the span member of a cache that has one.</summary>
public class HandlerKeyCacheTests
{
    internal const string Expected = "a:7";
    internal static readonly Func<CancellationToken, Task<string?>> Gen = _ => Task.FromResult<string?>("v");
    internal static readonly Func<int, CancellationToken, Task<string?>> StateGen = (_, _) => Task.FromResult<string?>("v");
    internal static readonly Func<CancellationToken, Task<GeneratedValue<string>>> GenExp = _ => Task.FromResult(new GeneratedValue<string>("v"));
    internal static readonly Func<int, CancellationToken, Task<GeneratedValue<string>>> StateGenExp = (_, _) => Task.FromResult(new GeneratedValue<string>("v"));
    internal static readonly CachePolicy? Policy = null;
    internal static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    internal static readonly DateTimeOffset Later = DateTimeOffset.UtcNow.AddHours(1);

    internal static readonly (string Tag, bool SpanPath, Func<ICache, string, int, Task> Call)[] UntypedCases =
    [
        ("GetAsync(policy)", true, (c, k, id) => c.GetAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("GetCacheEntryAsync(policy)", true, (c, k, id) => c.GetCacheEntryAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("ContainsAsync()", true, (c, k, id) => c.ContainsAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("RemoveAsync()", false, (c, k, id) => c.RemoveAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("TimeToLiveAsync()", true, (c, k, id) => c.TimeToLiveAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("ExpireTimeAsync()", true, (c, k, id) => c.ExpireTimeAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("SetAsync(value,policy)", true, (c, k, id) => c.SetAsync($"{k}:{id}", "x", Policy, Ct).AsTask()),
        ("SetAsync(value,TimeSpan,policy)", true, (c, k, id) => c.SetAsync($"{k}:{id}", "x", Minute, Policy, Ct).AsTask()),
        ("SetAsync(value,DateTimeOffset,policy)", true, (c, k, id) => c.SetAsync($"{k}:{id}", "x", Later, Policy, Ct).AsTask()),
        ("TryAddAsync(value,policy)", false, (c, k, id) => c.TryAddAsync($"{k}:{id}", "x", Policy, Ct).AsTask()),
        ("TryAddAsync(value,TimeSpan,policy)", false, (c, k, id) => c.TryAddAsync($"{k}:{id}", "x", Minute, Policy, Ct).AsTask()),
        ("TryAddAsync(value,DateTimeOffset,policy)", false, (c, k, id) => c.TryAddAsync($"{k}:{id}", "x", Later, Policy, Ct).AsTask()),
        ("RefreshAsync(policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("RefreshAsync(TimeSpan,policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Minute, Policy, Ct).AsTask()),
        ("RefreshAsync(DateTimeOffset,policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Later, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,policy)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", Gen, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,TimeSpan,policy)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", Gen, Minute, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset,policy)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", Gen, Later, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,policy)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, StateGen, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,TimeSpan,policy)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, StateGen, Minute, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset,policy)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, StateGen, Later, Policy, Ct).AsTask()),
        ("GetOrAddWithExpirationAsync(gen,policy)", true, (c, k, id) => c.GetOrAddWithExpirationAsync($"{k}:{id}", GenExp, Policy, Ct).AsTask()),
        ("GetOrAddWithExpirationAsync(state,gen,policy)", true, (c, k, id) => c.GetOrAddWithExpirationAsync($"{k}:{id}", 1, StateGenExp, Policy, Ct).AsTask()),
    ];

    internal static readonly (string Tag, bool SpanPath, Func<ICache<string>, string, int, Task> Call)[] TypedCases =
    [
        ("GetAsync()", true, (c, k, id) => c.GetAsync($"{k}:{id}", Ct).AsTask()),
        ("ContainsAsync()", true, (c, k, id) => c.ContainsAsync($"{k}:{id}", Ct).AsTask()),
        ("RemoveAsync()", false, (c, k, id) => c.RemoveAsync($"{k}:{id}", Ct).AsTask()),
        ("TimeToLiveAsync()", true, (c, k, id) => c.TimeToLiveAsync($"{k}:{id}", Ct).AsTask()),
        ("ExpireTimeAsync()", true, (c, k, id) => c.ExpireTimeAsync($"{k}:{id}", Ct).AsTask()),
        ("SetAsync(value)", true, (c, k, id) => c.SetAsync($"{k}:{id}", "x", Ct).AsTask()),
        ("SetAsync(value,TimeSpan)", true, (c, k, id) => c.SetAsync($"{k}:{id}", "x", Minute, Ct).AsTask()),
        ("SetAsync(value,DateTimeOffset)", true, (c, k, id) => c.SetAsync($"{k}:{id}", "x", Later, Ct).AsTask()),
        ("TryAddAsync(value)", false, (c, k, id) => c.TryAddAsync($"{k}:{id}", "x", Ct).AsTask()),
        ("TryAddAsync(value,TimeSpan)", false, (c, k, id) => c.TryAddAsync($"{k}:{id}", "x", Minute, Ct).AsTask()),
        ("TryAddAsync(value,DateTimeOffset)", false, (c, k, id) => c.TryAddAsync($"{k}:{id}", "x", Later, Ct).AsTask()),
        ("RefreshAsync()", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Ct).AsTask()),
        ("RefreshAsync(TimeSpan)", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Minute, Ct).AsTask()),
        ("RefreshAsync(DateTimeOffset)", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Later, Ct).AsTask()),
        ("GetOrAddAsync(gen)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", Gen, Ct).AsTask()),
        ("GetOrAddAsync(gen,TimeSpan)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", Gen, Minute, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", Gen, Later, Ct).AsTask()),
        ("GetOrAddAsync(state,gen)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, StateGen, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,TimeSpan)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, StateGen, Minute, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, StateGen, Later, Ct).AsTask()),
        ("GetOrAddWithExpirationAsync(gen)", true, (c, k, id) => c.GetOrAddWithExpirationAsync($"{k}:{id}", GenExp, Ct).AsTask()),
        ("GetOrAddWithExpirationAsync(state,gen)", true, (c, k, id) => c.GetOrAddWithExpirationAsync($"{k}:{id}", 1, StateGenExp, Ct).AsTask()),
    ];

    public static TheoryData<string> UntypedTags => [.. UntypedCases.Select(c => c.Tag)];

    public static TheoryData<string> UntypedSpanTags => [.. UntypedCases.Where(c => c.SpanPath).Select(c => c.Tag)];

    public static TheoryData<string> TypedTags => [.. TypedCases.Select(c => c.Tag)];

    public static TheoryData<string> TypedSpanTags => [.. TypedCases.Where(c => c.SpanPath).Select(c => c.Tag)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Cases_cover_every_handler_member()
    {
        UntypedCases.Select(c => c.Tag).Should().HaveCount(23).And.OnlyHaveUniqueItems();
        TypedCases.Select(c => c.Tag).Should().HaveCount(22).And.OnlyHaveUniqueItems();
    }

    [Theory]
    [MemberData(nameof(UntypedTags))]
    public async Task Without_span_capability_the_key_reaches_the_CacheKey_member(string tag)
    {
        var cache = Substitute.For<ICache>();

        await UntypedCases.Single(c => c.Tag == tag).Call(cache, "a", 7);

        AssertCacheKeyCall(cache, tag);
    }

    [Theory]
    [MemberData(nameof(TypedTags))]
    public async Task Typed_without_span_capability_the_key_reaches_the_CacheKey_member(string tag)
    {
        var cache = Substitute.For<ICache<string>>();

        await TypedCases.Single(c => c.Tag == tag).Call(cache, "a", 7);

        AssertCacheKeyCall(cache, tag);
    }

    [Theory]
    [MemberData(nameof(UntypedSpanTags))]
    public async Task With_span_capability_the_key_reaches_the_span_member(string tag)
    {
        var recorder = new SpanRecordingCache();

        await UntypedCases.Single(c => c.Tag == tag).Call(recorder, "a", 7);

        recorder.Calls.Should().Equal([(tag, Expected)]);
    }

    [Theory]
    [MemberData(nameof(TypedSpanTags))]
    public async Task Typed_with_span_capability_the_key_reaches_the_span_member_of_the_cache(string tag)
    {
        var inner = new SpanRecordingCache();
        var typed = new Cache<string>(inner, new DefaultCacheKeyStrategy());

        await TypedCases.Single(c => c.Tag == tag).Call(typed, "a", 7);

        inner.Calls.Should().ContainSingle().Which.Should().Match<(string Tag, string Key)>(c => c.Tag == InnerTag(tag) && c.Key.EndsWith(Expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(300)]
    [InlineData(5000)]
    public async Task Long_key_over_256_chars_works(int length)
    {
        var padding = new string('x', length);
        var recorder = new SpanRecordingCache();
        ICache spanCache = recorder;
        var plain = Substitute.For<ICache>();

        await spanCache.GetOrAddAsync($"{padding}:{7}", 1, StateGen, Policy, Ct);
        await plain.GetAsync<string>($"{padding}:{7}", Policy, Ct);

        recorder.Calls.Should().Equal([("GetOrAddAsync(state,gen,policy)", $"{padding}:7")]);
        plain.ReceivedCalls().Single().GetOriginalArguments()[0].Should().Be(new CacheKey($"{padding}:7"));
    }

    [Fact]
    public async Task A_null_generator_with_a_key_over_256_chars_throws_ArgumentNullException()
    {
        var padding = new string('x', 300);
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        ICache spanCache = new SpanRecordingCache();

        var calls = new Func<Task>[]
        {
            async () => await cache.GetOrAddWithExpirationAsync<string, int>($"{padding}:{7}", 1, null!, Policy, Ct),
            async () => await typed.GetOrAddWithExpirationAsync($"{padding}:{7}", 1, (Func<int, CancellationToken, Task<GeneratedValue<string>>>)null!, Ct),
            async () => await spanCache.GetOrAddWithExpirationAsync<string, int>($"{padding}:{7}", 1, null!, Policy, Ct),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowExactlyAsync<ArgumentNullException>();
        }
    }

    [Fact]
    public async Task Mock_records_handler_calls_as_CacheKey_calls()
    {
        var cache = Substitute.For<ICache>();
        var typed = Substitute.For<ICache<string>>();
        var id = 7;

        await cache.GetAsync<string>($"a:{id}", Policy, Ct);
        await cache.SetAsync($"a:{id}", "x", Minute, Policy, Ct);
        await cache.GetOrAddAsync($"a:{id}", 1, StateGen, Policy, Ct);
        await typed.RemoveAsync($"a:{id}", Ct);

        await cache.Received(1).GetAsync<string>(new CacheKey(Expected), Policy, Ct);
        await cache.Received(1).SetAsync(new CacheKey(Expected), "x", Minute, Policy, Ct);
        await cache.Received(1).GetOrAddAsync(new CacheKey(Expected), 1, StateGen, Policy, Ct);
        await typed.Received(1).RemoveAsync(new CacheKey(Expected), Ct);
    }

    [Fact]
    public async Task A_default_key_through_the_handler_behaves_as_default_CacheKey_on_the_real_cache()
    {
        using var cache = InMemoryMultilayer.Cache();
        ICache untyped = cache;

        (await Outcome(async () => await untyped.GetAsync<string>(default(CacheKey), Policy, Ct)))
            .Should().Be(await Outcome(async () => await untyped.GetAsync<string>(default, Policy, Ct)));
        (await Outcome(async () => await untyped.SetAsync(default(CacheKey), "v", Policy, Ct)))
            .Should().Be(await Outcome(async () => await untyped.SetAsync(default, "v", Policy, Ct)));
        (await Outcome(async () => await untyped.GetOrAddAsync(default(CacheKey), 1, StateGen, Policy, Ct)))
            .Should().Be(await Outcome(async () => await untyped.GetOrAddAsync(default, 1, StateGen, Policy, Ct)));
    }

    [Fact]
    public async Task A_default_key_through_the_handler_reaches_a_CacheKey_member_as_default()
    {
        var cache = Substitute.For<ICache>();

        await cache.GetAsync<string>(default, Policy, Ct);

        cache.ReceivedCalls().Single().GetOriginalArguments()[0].Should().Be(default(CacheKey));
    }

    private static async Task<string> Outcome<T>(Func<Task<T>> call)
    {
        try
        {
            return $"result: {await call()}";
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string InnerTag(string typedTag) =>
        typedTag.StartsWith("Contains", StringComparison.Ordinal) || typedTag.StartsWith("TimeToLive", StringComparison.Ordinal) || typedTag.StartsWith("ExpireTime", StringComparison.Ordinal)
            ? typedTag
            : typedTag.EndsWith("()", StringComparison.Ordinal) ? typedTag[..^1] + "policy)" : typedTag[..^1] + ",policy)";

    private static void AssertCacheKeyCall(object cache, string tag)
    {
        var call = cache.ReceivedCalls().Single();
        ShapeOf(call.GetMethodInfo()).Should().Be(tag);
        call.GetOriginalArguments()[0].Should().Be(new CacheKey(Expected));
    }

    private static string ShapeOf(MethodInfo method)
    {
        var parts = method.GetParameters()
            .Where(p => p.Name is not ("cacheKey" or "token"))
            .Select(p => p.Name switch { "generator" => "gen", "expiration" => p.ParameterType.Name, _ => p.Name! });
        return $"{method.Name}({string.Join(',', parts)})";
    }
}
