using System.Reflection;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests;

#pragma warning disable xUnit1051 // the cases pass the token explicitly where the call takes one

/// <summary>Every <c>IHashCache</c>, <c>IHashCache&lt;T&gt;</c> and <c>NullHashCache</c> member that takes an interpolated key reaches the same member a <c>CacheKey</c> call does, or the span member of a cache that has one.</summary>
public class HandlerKeyHashCacheTests
{
    internal const string Expected = "a:7";
    internal static readonly Func<CancellationToken, Task<IDictionary<string, string?>>> HGen = _ => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>());
    internal static readonly Func<int, CancellationToken, Task<IDictionary<string, string?>>> HStateGen = (_, _) => Task.FromResult<IDictionary<string, string?>>(new Dictionary<string, string?>());
    internal static readonly string[] Fields = ["f"];
    internal static readonly IDictionary<string, string?> Values = new Dictionary<string, string?> { ["f"] = "v" };
    internal static readonly IDictionary<string, string?> Meta = new Dictionary<string, string?> { ["m"] = "v" };
    internal static readonly HashCacheEntryOptions Options = new();
    internal static readonly CachePolicy? Policy = null;
    internal static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    internal static readonly DateTimeOffset Later = DateTimeOffset.UtcNow.AddHours(1);

    internal static readonly (string Tag, bool SpanPath, Func<IHashCache, string, int, Task> Call)[] UntypedCases =
    [
        ("GetItemAsync(field,policy)", true, (c, k, id) => c.GetItemAsync<string>($"{k}:{id}", "f", Policy, Ct).AsTask()),
        ("GetAsync(fields,policy)", true, (c, k, id) => c.GetAsync<string>($"{k}:{id}", Fields, Policy, Ct).AsTask()),
        ("GetAsync(policy)", true, (c, k, id) => c.GetAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("GetCacheEntryAsync(policy)", true, (c, k, id) => c.GetCacheEntryAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,policy)", true, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,TimeSpan,policy)", true, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Minute, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset,policy)", true, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Later, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset,setOption,policy)", true, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Later, HashCacheSetOption.KeyReplace, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,policy)", true, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,TimeSpan,policy)", true, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Minute, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset,policy)", true, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Later, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset,setOption,policy)", true, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Later, HashCacheSetOption.KeyReplace, Policy, Ct).AsTask()),
        ("SetAsync(values,policy)", true, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Policy, Ct).AsTask()),
        ("SetAsync(values,TimeSpan,policy)", true, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Minute, Policy, Ct).AsTask()),
        ("SetAsync(values,DateTimeOffset,policy)", true, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Later, Policy, Ct).AsTask()),
        ("SetAsync(values,options,policy)", true, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Options, Policy, Ct).AsTask()),
        ("RefreshAsync(policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("RefreshAsync(TimeSpan,policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Minute, Policy, Ct).AsTask()),
        ("RefreshAsync(DateTimeOffset,policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Later, Policy, Ct).AsTask()),
        ("RefreshAsync(options,policy)", false, (c, k, id) => c.RefreshAsync<string>($"{k}:{id}", Options, Policy, Ct).AsTask()),
        ("RemoveAsync()", false, (c, k, id) => c.RemoveAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("RemoveFieldsAsync(fields)", false, (c, k, id) => c.RemoveFieldsAsync<string>($"{k}:{id}", Fields, Ct).AsTask()),
        ("ContainsAsync()", true, (c, k, id) => c.ContainsAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("TimeToLiveAsync()", true, (c, k, id) => c.TimeToLiveAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("ExpireTimeAsync()", true, (c, k, id) => c.ExpireTimeAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("GetMetadataAsync()", true, (c, k, id) => c.GetMetadataAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("SetMetadataAsync(metadata)", false, (c, k, id) => c.SetMetadataAsync<string>($"{k}:{id}", Meta, Ct).AsTask()),
    ];

    internal static readonly (string Tag, bool SpanPath, Func<IHashCache<string>, string, int, Task> Call)[] TypedCases =
    [
        ("GetItemAsync(field)", true, (c, k, id) => c.GetItemAsync($"{k}:{id}", "f", Ct).AsTask()),
        ("GetAsync(fields)", true, (c, k, id) => c.GetAsync($"{k}:{id}", Fields, Ct).AsTask()),
        ("GetAsync()", true, (c, k, id) => c.GetAsync($"{k}:{id}", Ct).AsTask()),
        ("GetOrAddAsync(gen)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", HGen, Ct).AsTask()),
        ("GetOrAddAsync(gen,TimeSpan)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", HGen, Minute, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", HGen, Later, Ct).AsTask()),
        ("GetOrAddAsync(state,gen)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, HStateGen, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,TimeSpan)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, HStateGen, Minute, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset)", true, (c, k, id) => c.GetOrAddAsync($"{k}:{id}", 1, HStateGen, Later, Ct).AsTask()),
        ("GetCacheEntryAsync()", true, (c, k, id) => c.GetCacheEntryAsync($"{k}:{id}", Ct).AsTask()),
        ("SetAsync(values)", true, (c, k, id) => c.SetAsync($"{k}:{id}", Values, Ct).AsTask()),
        ("SetAsync(values,TimeSpan)", true, (c, k, id) => c.SetAsync($"{k}:{id}", Values, Minute, Ct).AsTask()),
        ("SetAsync(values,DateTimeOffset)", true, (c, k, id) => c.SetAsync($"{k}:{id}", Values, Later, Ct).AsTask()),
        ("SetAsync(values,options)", true, (c, k, id) => c.SetAsync($"{k}:{id}", Values, Options, Ct).AsTask()),
        ("RefreshAsync()", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Ct).AsTask()),
        ("RefreshAsync(TimeSpan)", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Minute, Ct).AsTask()),
        ("RefreshAsync(DateTimeOffset)", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Later, Ct).AsTask()),
        ("RefreshAsync(options)", false, (c, k, id) => c.RefreshAsync($"{k}:{id}", Options, Ct).AsTask()),
        ("RemoveAsync()", false, (c, k, id) => c.RemoveAsync($"{k}:{id}", Ct).AsTask()),
        ("RemoveFieldsAsync(fields)", false, (c, k, id) => c.RemoveFieldsAsync($"{k}:{id}", Fields, Ct).AsTask()),
        ("ContainsAsync()", true, (c, k, id) => c.ContainsAsync($"{k}:{id}", Ct).AsTask()),
        ("TimeToLiveAsync()", true, (c, k, id) => c.TimeToLiveAsync($"{k}:{id}", Ct).AsTask()),
        ("ExpireTimeAsync()", true, (c, k, id) => c.ExpireTimeAsync($"{k}:{id}", Ct).AsTask()),
        ("GetMetadataAsync()", true, (c, k, id) => c.GetMetadataAsync($"{k}:{id}", Ct).AsTask()),
        ("SetMetadataAsync(metadata)", false, (c, k, id) => c.SetMetadataAsync($"{k}:{id}", Meta, Ct).AsTask()),
    ];

    internal static readonly (string Tag, bool SpanPath, Func<NullHashCache, string, int, Task> Call)[] NullCases =
    [
        ("ContainsAsync()", false, (c, k, id) => c.ContainsAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("ExpireTimeAsync()", false, (c, k, id) => c.ExpireTimeAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("GetAsync(fields,policy)", false, (c, k, id) => c.GetAsync<string>($"{k}:{id}", Fields, Policy, Ct).AsTask()),
        ("GetAsync(policy)", false, (c, k, id) => c.GetAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("GetCacheEntryAsync(policy)", false, (c, k, id) => c.GetCacheEntryAsync<string>($"{k}:{id}", Policy, Ct).AsTask()),
        ("GetMetadataAsync()", false, (c, k, id) => c.GetMetadataAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("GetItemAsync(field,policy)", false, (c, k, id) => c.GetItemAsync<string>($"{k}:{id}", "f", Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,policy)", false, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,TimeSpan,policy)", false, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Minute, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset,policy)", false, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Later, Policy, Ct).AsTask()),
        ("GetOrAddAsync(gen,DateTimeOffset,setOption,policy)", false, (c, k, id) => c.GetOrAddAsync<string>($"{k}:{id}", HGen, Later, HashCacheSetOption.KeyReplace, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,policy)", false, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,TimeSpan,policy)", false, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Minute, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset,policy)", false, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Later, Policy, Ct).AsTask()),
        ("GetOrAddAsync(state,gen,DateTimeOffset,setOption,policy)", false, (c, k, id) => c.GetOrAddAsync<string, int>($"{k}:{id}", 1, HStateGen, Later, HashCacheSetOption.KeyReplace, Policy, Ct).AsTask()),
        ("RemoveAsync()", false, (c, k, id) => c.RemoveAsync<string>($"{k}:{id}", Ct).AsTask()),
        ("SetAsync(values,policy)", false, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Policy, Ct).AsTask()),
        ("SetAsync(values,TimeSpan,policy)", false, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Minute, Policy, Ct).AsTask()),
        ("SetAsync(values,DateTimeOffset,policy)", false, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Later, Policy, Ct).AsTask()),
        ("SetAsync(values,options,policy)", false, (c, k, id) => c.SetAsync<string>($"{k}:{id}", Values, Options, Policy, Ct).AsTask()),
        ("TimeToLiveAsync()", false, (c, k, id) => c.TimeToLiveAsync<string>($"{k}:{id}", Ct).AsTask()),
    ];

    public static TheoryData<string> UntypedTags => [.. UntypedCases.Select(c => c.Tag)];

    public static TheoryData<string> UntypedSpanTags => [.. UntypedCases.Where(c => c.SpanPath).Select(c => c.Tag)];

    public static TheoryData<string> TypedTags => [.. TypedCases.Select(c => c.Tag)];

    public static TheoryData<string> TypedSpanTags => [.. TypedCases.Where(c => c.SpanPath).Select(c => c.Tag)];

    public static TheoryData<string> NullTags => [.. NullCases.Select(c => c.Tag)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Cases_cover_every_handler_member()
    {
        UntypedCases.Select(c => c.Tag).Should().HaveCount(27).And.OnlyHaveUniqueItems();
        TypedCases.Select(c => c.Tag).Should().HaveCount(25).And.OnlyHaveUniqueItems();
        NullCases.Select(c => c.Tag).Should().BeSubsetOf(UntypedCases.Select(c => c.Tag));
        typeof(IHashCache).GetMethods().Count(TakesHandler).Should().Be(UntypedCases.Length);
        typeof(IHashCache<>).GetMethods().Count(TakesHandler).Should().Be(TypedCases.Length);
        typeof(NullHashCache).GetMethods().Count(TakesHandler).Should().Be(NullCases.Length);
    }

    [Theory]
    [MemberData(nameof(UntypedTags))]
    public async Task Without_span_capability_the_key_reaches_the_CacheKey_member(string tag)
    {
        var cache = Mock(tag);

        await UntypedCases.Single(c => c.Tag == tag).Call(cache, "a", 7);

        AssertCacheKeyCall(cache, tag);
    }

    [Theory]
    [MemberData(nameof(TypedTags))]
    public async Task Typed_without_span_capability_the_key_reaches_the_CacheKey_member(string tag)
    {
        var cache = TypedMock(tag);

        await TypedCases.Single(c => c.Tag == tag).Call(cache, "a", 7);

        AssertCacheKeyCall(cache, tag);
    }

    [Theory]
    [MemberData(nameof(UntypedSpanTags))]
    public async Task With_span_capability_the_key_reaches_the_span_member(string tag)
    {
        var recorder = new SpanRecordingHashCache();

        await UntypedCases.Single(c => c.Tag == tag).Call(recorder, "a", 7);

        recorder.Calls.Should().Equal([(tag, Expected)]);
    }

    [Theory]
    [MemberData(nameof(TypedSpanTags))]
    public async Task Typed_with_span_capability_the_key_reaches_the_span_member_of_the_cache(string tag)
    {
        var inner = new SpanRecordingHashCache();
        IHashCache<string> typed = new HashCache<string>(inner, new DefaultCacheKeyStrategy());

        await TypedCases.Single(c => c.Tag == tag).Call(typed, "a", 7);

        inner.Calls.Should().ContainSingle().Which.Should().Match<(string Tag, string Key)>(c => c.Tag == InnerTag(tag) && c.Key.EndsWith(Expected, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(UntypedSpanTags))]
    public async Task A_key_over_256_chars_reaches_the_span_member_whole(string tag)
    {
        var recorder = new SpanRecordingHashCache();
        var padding = new string('x', 5000);

        await UntypedCases.Single(c => c.Tag == tag).Call(recorder, padding, 7);

        recorder.Calls.Should().Equal([(tag, $"{padding}:7")]);
    }

    [Theory]
    [MemberData(nameof(UntypedTags))]
    public async Task A_key_over_256_chars_reaches_the_CacheKey_member_whole(string tag)
    {
        var cache = Mock(tag);
        var padding = new string('x', 5000);

        await UntypedCases.Single(c => c.Tag == tag).Call(cache, padding, 7);

        cache.ReceivedCalls().Single().GetOriginalArguments()[0].Should().Be(new CacheKey($"{padding}:7"));
    }

    [Fact]
    public async Task A_typed_key_under_the_span_limit_reaches_the_span_member_whole()
    {
        var inner = new SpanRecordingHashCache();
        IHashCache<string> typed = new HashCache<string>(inner, new DefaultCacheKeyStrategy());
        var padding = new string('x', 100);

        await typed.GetAsync($"{padding}:{7}", Ct);

        inner.Calls.Should().ContainSingle().Which.Key.Should().EndWith($"{padding}:7");
    }

    [Theory]
    [InlineData(300)]
    [InlineData(5000)]
    public async Task A_typed_key_over_256_chars_arrives_whole_by_the_CacheKey_member(int length)
    {
        var inner = Substitute.For<IHashCache>();
        IHashCache<string> typed = new HashCache<string>(inner, new DefaultCacheKeyStrategy());
        var padding = new string('x', length);

        await typed.GetAsync($"{padding}:{7}", Ct);

        inner.ReceivedCalls().Single().GetOriginalArguments()[0].Should().BeOfType<CacheKey>().Which.ToString().Should().EndWith($"{padding}:7");
    }

    [Fact]
    public async Task A_null_generator_with_a_key_over_256_chars_throws_ArgumentNullException()
    {
        var padding = new string('x', 300);
        var cache = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();
        IHashCache spanCache = new SpanRecordingHashCache();

        var calls = new Func<Task>[]
        {
            async () => await cache.GetOrAddAsync<string, int>($"{padding}:{7}", 1, null!, Policy, Ct),
            async () => await cache.GetOrAddAsync<string, int>($"{padding}:{7}", 1, null!, Minute, Policy, Ct),
            async () => await typed.GetOrAddAsync($"{padding}:{7}", 1, (Func<int, CancellationToken, Task<IDictionary<string, string?>>>)null!, Ct),
            async () => await spanCache.GetOrAddAsync<string, int>($"{padding}:{7}", 1, null!, Later, HashCacheSetOption.KeyReplace, Policy, Ct),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowExactlyAsync<ArgumentNullException>();
        }
    }

    [Theory]
    [MemberData(nameof(NullTags))]
    public async Task NullHashCache_takes_the_interpolated_key_in_every_shape(string tag)
    {
        var error = await Record.ExceptionAsync(() => NullCases.Single(c => c.Tag == tag).Call(NullHashCache.Instance, "a", 7));

        error.Should().BeNull();
    }

    [Fact]
    public async Task Mock_records_handler_calls_as_CacheKey_calls()
    {
        var cache = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();
        var id = 7;

        await cache.GetAsync<string>($"a:{id}", Policy, Ct);
        await cache.SetAsync($"a:{id}", Values, Minute, Policy, Ct);
        await cache.GetItemAsync<string>($"a:{id}", "f", Policy, Ct);
        await typed.RemoveAsync($"a:{id}", Ct);

        await cache.Received(1).GetAsync<string>(new CacheKey(Expected), Policy, Ct);
        await cache.Received(1).SetAsync(new CacheKey(Expected), Values, Minute, Policy, Ct);
        await cache.Received(1).GetItemAsync<string>(new CacheKey(Expected), "f", Policy, Ct);
        await typed.Received(1).RemoveAsync(new CacheKey(Expected), Ct);
    }

    [Fact]
    public async Task A_bare_default_key_reaches_a_CacheKey_member_as_default()
    {
        var cache = Substitute.For<IHashCache>();
        var typed = Substitute.For<IHashCache<string>>();

        await cache.GetAsync<string>(default, Policy, Ct);
        await typed.GetAsync(default, Ct);

        cache.ReceivedCalls().Single().GetOriginalArguments()[0].Should().Be(default(CacheKey));
        typed.ReceivedCalls().Single().GetOriginalArguments()[0].Should().Be(default(CacheKey));
    }

    [Fact]
    public async Task An_empty_interpolation_is_the_empty_key_not_the_default_key()
    {
        var cache = Substitute.For<IHashCache>();

        await cache.GetAsync<string>($"", Policy, Ct);

        cache.ReceivedCalls().Single().GetOriginalArguments()[0].Should().Be(new CacheKey(""));
    }

    /// <summary>Removing single fields needs the capability interface, so its mock has it and the call reaches the <c>CacheKey</c> member there.</summary>
    internal static IHashCache Mock(string tag) =>
        tag.StartsWith("RemoveFieldsAsync", StringComparison.Ordinal) ? Substitute.For<IHashCache, ISpanKeyHashCache>() : Substitute.For<IHashCache>();

    internal static IHashCache<string> TypedMock(string tag) =>
        tag.StartsWith("RemoveFieldsAsync", StringComparison.Ordinal) ? Substitute.For<IHashCache<string>, ISpanKeyHashCache<string>>() : Substitute.For<IHashCache<string>>();

    private static bool TakesHandler(MethodInfo method) => method.GetParameters().Any(p => p.ParameterType.GetElementType() == typeof(CacheKeyHandler));

    private static string InnerTag(string typedTag) =>
        typedTag.StartsWith("Contains", StringComparison.Ordinal) || typedTag.StartsWith("TimeToLive", StringComparison.Ordinal) || typedTag.StartsWith("ExpireTime", StringComparison.Ordinal) || typedTag.StartsWith("GetMetadata", StringComparison.Ordinal)
            ? typedTag
            : typedTag.EndsWith("()", StringComparison.Ordinal) ? typedTag[..^1] + "policy)" : typedTag[..^1] + ",policy)";

    private static void AssertCacheKeyCall(object cache, string tag)
    {
        var call = cache.ReceivedCalls().Single();
        SpanRecordingHashCache.Shape(call.GetMethodInfo()).Should().Be(tag);
        call.GetOriginalArguments()[0].Should().Be(new CacheKey(Expected));
    }
}
