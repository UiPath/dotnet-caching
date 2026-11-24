using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// The IHashCache<T> surface of a routed hash cache: every member has one rule in the table, and each span-key member is a forward to the member of the same name.
public sealed class HashSurfaceRulesTests : RouterTestBase
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    public static TheoryData<string> SpanCases => [.. SpanCalls.Keys];

    private static Dictionary<string, (string Key, Func<ISpanKeyHashCache<Item>, char[], Task<object?>> Span, Func<IHashCache<Item>, Task<object?>> Plain)> SpanCalls { get; } = BuildSpanCalls();

    [Fact]
    public void Every_member_of_IHashCache_has_a_rule_and_the_table_names_no_member_that_is_not_there()
    {
        var members = MemberSignature.Required(typeof(IHashCache<>)).Select(MemberSignature.Of).ToList();

        members.Should().HaveCount(25).And.OnlyHaveUniqueItems();
        members.Where(m => !HashMemberRules.Table.ContainsKey(m)).Should().BeEmpty("every abstract member of IHashCache<T> needs a rule");
        HashMemberRules.Table.Keys.Where(k => !members.Contains(k)).Should().BeEmpty("a rule for a member that is not on the interface is stale");
        HashMemberRules.Table.Values.Should().OnlyContain(r => r.Note.Length > 0);
    }

    [Fact]
    public void A_routed_hash_cache_implements_every_member_explicitly_and_the_interface_has_no_default_member_a_class_could_override()
    {
        var map = typeof(RoutedHashCache<Item>).GetInterfaceMap(typeof(IHashCache<Item>));

        foreach (var method in map.InterfaceMethods.Where(m => m.IsAbstract))
        {
            var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, method)];
            target.DeclaringType.Should().Be<RoutedHashCache<Item>>(method.ToString());
            target.IsPrivate.Should().BeTrue("a member is implemented explicitly: " + method);
        }

        typeof(IHashCache<>).GetMethods().Count(m => !m.IsAbstract && !m.IsSpecialName && !m.IsFinal && m.IsVirtual).Should().Be(0, "only sealed members are allowed as defaults");
    }

    [Fact]
    public void The_refused_members_are_the_ones_the_source_has_no_equivalent_for_and_the_classes_count_as_the_table_says()
    {
        var refused = HashMemberRules.Table.Where(r => r.Value.RefusedOnSource).Select(r => r.Key.Split('(')[0]).Distinct().Order(StringComparer.Ordinal);

        refused.Should().Equal("ExpireTimeAsync", "RefreshAsync", "SetMetadataAsync", "TimeToLiveAsync");
        HashMemberRules.Table.Values.GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Count())
            .Should().Equal(new Dictionary<RuleClass, int> { [RuleClass.Local] = 1, [RuleClass.Routed] = 17, [RuleClass.Backend] = 7 });
    }

    [Fact]
    public void A_routed_hash_cache_implements_every_span_member_and_the_field_removal_of_the_span_interface()
    {
        var map = typeof(RoutedHashCache<Item>).GetInterfaceMap(typeof(ISpanKeyHashCache<Item>));
        var abstractMembers = map.InterfaceMethods.Where(m => m.IsAbstract).ToList();

        abstractMembers.Should().HaveCount(18);
        foreach (var method in map.InterfaceMethods.Where(m => m.Name == "RemoveFieldsAsync" || m.IsAbstract))
        {
            var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, method)];
            target.DeclaringType.Should().Be<RoutedHashCache<Item>>(method.ToString());
        }

        SpanCalls.Should().HaveCount(18, "each of the 18 span members is exercised");
    }

    [Theory]
    [MemberData(nameof(SpanCases))]
    public async Task A_span_member_reaches_its_cache_key_member_with_the_same_arguments(string member)
    {
        var (key, span, plain) = SpanCalls[member];
        var viaSpan = await RunAsync(node => span(node.Cache, key.ToCharArray()));
        var viaKey = await RunAsync(node => plain(node.Cache));

        viaSpan.Result.Should().BeEquivalentTo(viaKey.Result);

        viaSpan.Log.Should().NotBeEmpty().And.Equal(viaKey.Log);
        viaSpan.Fields.Should().BeEquivalentTo(viaKey.Fields);
        viaSpan.Options.Should().Be(viaKey.Options);
    }

    [Fact]
    public async Task RemoveFieldsAsync_reaches_the_field_form_and_not_the_removal_of_the_hash()
    {
        var node = Seeded("k");
        ISpanKeyHashCache<Item> span = node.Cache;

        (await span.RemoveFieldsAsync(new CacheKey("k"), ["f"], Ct)).Should().BeTrue();
        (await node.Surface.RemoveFieldsAsync(new CacheKey("k"), ["g"], Ct)).Should().BeTrue();

        node.Library.FieldRemoves.Should().Be(2);
        node.Library.Removes.Should().Be(0);
        node.Library.LastRemovedFields.Should().Equal("g");
        await node.DisposeAsync();
    }

    private static Dictionary<string, (string Key, Func<ISpanKeyHashCache<Item>, char[], Task<object?>> Span, Func<IHashCache<Item>, Task<object?>> Plain)> BuildSpanCalls()
    {
        var token = CancellationToken.None;
        var now = new DateTimeOffset(2026, 11, 18, 13, 0, 0, TimeSpan.Zero);
        static Task<IDictionary<string, Item?>> Make(CancellationToken cancellation) => Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["f"] = new Item("made") });
        static Task<IDictionary<string, Item?>> Made(string state, CancellationToken cancellation) => Task.FromResult<IDictionary<string, Item?>>(new Dictionary<string, Item?> { ["f"] = new Item(state) });
        IDictionary<string, Item?> values = new Dictionary<string, Item?> { ["f"] = new Item("v") };
        var options = new HashCacheEntryOptions(SetOption: HashCacheSetOption.HashReplace, TimeToLive: Life);
        return new Dictionary<string, (string, Func<ISpanKeyHashCache<Item>, char[], Task<object?>>, Func<IHashCache<Item>, Task<object?>>)>
        {
            ["GetItemAsync"] = ("k", (s, k) => Box(s.GetItemAsync(k.AsSpan(), "f", token)), c => Box(c.GetItemAsync(new CacheKey("k"), "f", token))),
            ["GetAsync"] = ("k", (s, k) => Box(s.GetAsync(k.AsSpan(), token)), c => Box(c.GetAsync(new CacheKey("k"), token))),
            ["GetAsync(fields)"] = ("k", (s, k) => Box(s.GetAsync(k.AsSpan(), ["f"], token)), c => Box(c.GetAsync(new CacheKey("k"), ["f"], token))),
            ["GetCacheEntryAsync"] = ("k", (s, k) => Box(s.GetCacheEntryAsync(k.AsSpan(), token)), c => Box(c.GetCacheEntryAsync(new CacheKey("k"), token))),
            ["GetOrAddAsync"] = ("g", (s, k) => Box(s.GetOrAddAsync(k.AsSpan(), Make, token)), c => Box(c.GetOrAddAsync(new CacheKey("g"), Make, token))),
            ["GetOrAddAsync(TimeSpan)"] = ("g", (s, k) => Box(s.GetOrAddAsync(k.AsSpan(), Make, Life, token)), c => Box(c.GetOrAddAsync(new CacheKey("g"), Make, Life, token))),
            ["GetOrAddAsync(DateTimeOffset)"] = ("g", (s, k) => Box(s.GetOrAddAsync(k.AsSpan(), Make, now.AddMinutes(10), token)), c => Box(c.GetOrAddAsync(new CacheKey("g"), Make, now.AddMinutes(10), token))),
            ["GetOrAddAsync<TState>"] = ("g", (s, k) => Box(s.GetOrAddAsync(k.AsSpan(), "s", Made, token)), c => Box(c.GetOrAddAsync(new CacheKey("g"), "s", Made, token))),
            ["GetOrAddAsync<TState>(TimeSpan)"] = ("g", (s, k) => Box(s.GetOrAddAsync(k.AsSpan(), "s", Made, Life, token)), c => Box(c.GetOrAddAsync(new CacheKey("g"), "s", Made, Life, token))),
            ["GetOrAddAsync<TState>(DateTimeOffset)"] = ("g", (s, k) => Box(s.GetOrAddAsync(k.AsSpan(), "s", Made, now.AddMinutes(10), token)), c => Box(c.GetOrAddAsync(new CacheKey("g"), "s", Made, now.AddMinutes(10), token))),
            ["ContainsAsync"] = ("k", (s, k) => Box(s.ContainsAsync(k.AsSpan(), token)), c => Box(c.ContainsAsync(new CacheKey("k"), token))),
            ["TimeToLiveAsync"] = ("k", (s, k) => Box(s.TimeToLiveAsync(k.AsSpan(), token)), c => Box(c.TimeToLiveAsync(new CacheKey("k"), token))),
            ["ExpireTimeAsync"] = ("k", (s, k) => Box(s.ExpireTimeAsync(k.AsSpan(), token)), c => Box(c.ExpireTimeAsync(new CacheKey("k"), token))),
            ["GetMetadataAsync"] = ("k", (s, k) => Box(s.GetMetadataAsync(k.AsSpan(), token)), c => Box(c.GetMetadataAsync(new CacheKey("k"), token))),
            ["SetAsync"] = ("k", (s, k) => Box(s.SetAsync(k.AsSpan(), values, token)), c => Box(c.SetAsync(new CacheKey("k"), values, token))),
            ["SetAsync(TimeSpan)"] = ("k", (s, k) => Box(s.SetAsync(k.AsSpan(), values, Life, token)), c => Box(c.SetAsync(new CacheKey("k"), values, Life, token))),
            ["SetAsync(DateTimeOffset)"] = ("k", (s, k) => Box(s.SetAsync(k.AsSpan(), values, now.AddMinutes(10), token)), c => Box(c.SetAsync(new CacheKey("k"), values, now.AddMinutes(10), token))),
            ["SetAsync(options)"] = ("k", (s, k) => Box(s.SetAsync(k.AsSpan(), values, options, token)), c => Box(c.SetAsync(new CacheKey("k"), values, options, token))),
        };
    }

    private static async Task<object?> Box<TResult>(ValueTask<TResult> call) => await call.ConfigureAwait(false);

    private HashTestNode Seeded(string key)
    {
        var store = new Dictionary<string, StoredHash>
        {
            [key] = new() { Fields = new Dictionary<string, Item?> { ["f"] = new Item("1"), ["g"] = new Item("2") }, Expiration = Clock.GetUtcNow().AddMinutes(10), Metadata = new Dictionary<string, string?> { ["m"] = "x" } },
        };
        return HashTestNode.Create(Clock, CacheName, store: store);
    }

    private async Task<Run> RunAsync(Func<HashTestNode, Task<object?>> call)
    {
        await using var node = Seeded("k");
        var result = await call(node);
        return new Run(result, node.Library.Log, node.Library.LastFields, node.Library.LastOptions);
    }

    private sealed record Run(object? Result, IReadOnlyList<string> Log, string[]? Fields, HashWriteOptions? Options);
}
