using System.Reflection;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests;

// The ICache<T> surface of a routed cache: every member has one rule in the table, and the rule is what the member does on the library path, on the source path and in bypass.
public sealed class CacheSurfaceRulesTests : RouterTestBase
{
    private static readonly TimeSpan Life = TimeSpan.FromMinutes(5);

    public static TheoryData<string> Modes => ["library", "source", "bypass"];

    [Fact]
    public void A_member_without_a_cache_signature_keeps_its_reflection_name()
    {
        MemberSignature.Of(typeof(Item)).Should().Be(nameof(Item));
    }

    [Fact]
    public void Every_member_of_ICache_has_a_rule_and_the_table_names_no_member_that_is_not_there()
    {
        var members = MemberSignature.Required().Select(MemberSignature.Of).ToList();

        members.Should().OnlyHaveUniqueItems();
        members.Where(m => !CacheMemberRules.Table.ContainsKey(m)).Should().BeEmpty("every abstract member of ICache<T> needs a rule");
        CacheMemberRules.Table.Keys.Where(k => !members.Contains(k)).Should().BeEmpty("a rule for a member that is not on the interface is stale");
        CacheMemberRules.Table.Values.Should().OnlyContain(r => r.Note.Length > 0);
    }

    [Fact]
    public void A_routed_cache_implements_every_member_explicitly_and_the_interface_gained_no_default_member_for_it()
    {
        var map = typeof(RoutedCache<Item>).GetInterfaceMap(typeof(ICache<Item>));
        var implemented = map.TargetMethods.Where(m => m.IsPrivate && m.IsFinal).Select(m => m.Name).ToHashSet();

        foreach (var method in map.InterfaceMethods.Where(m => m.IsAbstract))
        {
            var target = map.TargetMethods[Array.IndexOf(map.InterfaceMethods, method)];
            target.DeclaringType.Should().Be<RoutedCache<Item>>(method.ToString());
            target.IsPrivate.Should().BeTrue("a member is implemented explicitly: " + method);
        }

        implemented.Should().NotBeEmpty();
        typeof(ICache<>).GetMethods().Count(m => !m.IsAbstract && !m.IsSpecialName && !m.IsFinal && m.IsVirtual).Should().Be(0, "a default member that a derived class could override is the mock issue; only sealed members are allowed");
    }

    [Fact]
    public void The_refused_members_are_the_ones_the_source_has_no_equivalent_for()
    {
        var refused = CacheMemberRules.Table.Where(r => r.Value.RefusedOnSource).Select(r => r.Key.Split('(')[0]).Distinct().Order(StringComparer.Ordinal);

        refused.Should().Equal("ExpireTimeAsync", "RefreshAsync", "SetIfChangedAsync", "TimeToLiveAsync", "TryAddAsync", "TryFillAsync");
        CacheMemberRules.Table.Values.GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Count())
            .Should().Equal(new Dictionary<RuleClass, int> { [RuleClass.Local] = 1, [RuleClass.Routed] = 28, [RuleClass.Backend] = 9 });
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Get_set_remove_and_get_or_add_go_to_the_path_the_mode_picks_and_nothing_else(string mode)
    {
        var (node, target, other) = Node(mode);
        ICache<Item> cache = node.Cache;

        (await cache.SetAsync("a", new Item("1"), Life, Ct)).Should().BeTrue();
        (await cache.GetAsync("a", Ct))!.Name.Should().Be("1");
        (await cache.ContainsAsync("a", Ct)).Should().BeTrue();
        (await cache.GetOrAddAsync("a", _ => Task.FromResult<Item?>(new Item("never")), Life, Ct))!.Name.Should().Be("1");
        (await cache.GetOrAddAsync("g", "state", (s, _) => Task.FromResult<Item?>(new Item(s)), Ct))!.Name.Should().Be("state");
        (await cache.GetOrAddWithExpirationAsync("e", _ => Task.FromResult(new GeneratedValue<Item>(new Item("exp"), Clock.GetUtcNow().AddMinutes(3))), Ct))!.Name.Should().Be("exp");
        (await cache.RemoveAsync("a", Ct)).Should().BeTrue();
        (await cache.GetAsync("a", Ct)).Should().BeNull();

        target.Writes.Should().BeGreaterThanOrEqualTo(3);
        target.Removes.Should().Be(1);
        other.Writes.Should().Be(0);
        other.Removes.Should().Be(0);
        other.Reads.Should().Be(0);
        target.LastDeadline.Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_generator_that_names_a_life_has_it_applied_to_the_write(string mode)
    {
        var (node, target, _) = Node(mode);
        ICache<Item> cache = node.Cache;

        await cache.GetOrAddWithExpirationAsync("e", _ => Task.FromResult(new GeneratedValue<Item>(new Item("exp"), Clock.GetUtcNow().AddMinutes(3))), Ct);

        target.LastDeadline.Should().Be(Clock.GetUtcNow().AddMinutes(3));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_many_key_forms_run_each_key_through_the_same_rules(string mode)
    {
        var (node, target, other) = Node(mode);
        ICache<Item> cache = node.Cache;

        (await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("a", new Item("1")), new KeyValuePair<CacheKey, Item?>("b", new Item("2"))], Life, Ct)).Should().BeTrue();
        var read = await cache.GetAsync(["a", "b", "c"], Ct);
        read.Select(r => r.Value?.Name).Should().Equal("1", "2", null);

        var made = await cache.GetOrAddAsync(
            [new KeyValuePair<CacheKey, string>("b", "sb"), new KeyValuePair<CacheKey, string>("c", "sc")],
            (states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, Item?>(s, new Item("made-" + s))).ToArray()),
            Ct);
        made.Select(m => m.Value?.Name).Should().Equal("2", "made-sc");
        (await cache.RemoveAsync(["a", "b"], Ct)).Should().BeTrue();
        (await cache.RemoveManyAsync(["c"], Ct)).Should().BeEmpty("the many-key forms report the keys that failed");
        (await cache.SetManyAsync([new KeyValuePair<CacheKey, Item?>("d", new Item("4"))], Ct)).Should().BeEmpty();

        target.Removes.Should().Be(3);
        other.Writes.Should().Be(0);
        other.Removes.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_null_value_removes_and_the_library_swaps_a_changed_value_atomically_while_the_source_refuses_the_swap(string mode)
    {
        var (node, target, _) = Node(mode);
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);

        if (mode == "library")
        {
            (await cache.SetIfChangedAsync("k", new Item("1"), EqualityComparer<Item>.Default, Life, Ct)).Outcome.Should().Be(ChangeOutcome.Unchanged);
            (await cache.SetIfChangedAsync("k", new Item("2"), EqualityComparer<Item>.Default, Life, Ct)).Outcome.Should().Be(ChangeOutcome.Replaced);
            (await cache.SetIfChangedAsync("n", new Item("3"), EqualityComparer<Item>.Default, Life, Ct)).Outcome.Should().Be(ChangeOutcome.Filled);
            target.Log.Count(e => e.StartsWith("swap:", StringComparison.Ordinal)).Should().Be(3, "the swap is the library's own, not a read, a compare and a write of the router's");
        }
        else
        {
            var swap = async () => await cache.SetIfChangedAsync("k", new Item("2"), EqualityComparer<Item>.Default, Life, Ct);
            await swap.Should().ThrowAsync<NotSupportedException>();
        }

        (await cache.SetAsync("k", null, Life, Ct)).Should().BeTrue();
        (await cache.GetAsync("k", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_write_that_was_not_sent_or_has_no_known_outcome_is_false_as_the_librarys_is_and_never_throws()
    {
        var (node, target, other) = Node("library");
        ICache<Item> cache = node.Cache;

        // Neither client takes the write: the library, and the source it falls back to.
        other.WriteOutcome = WriteOutcome.NotSent;
        target.WriteOutcome = WriteOutcome.NotSent;
        (await cache.SetAsync("k", new Item("1"), Life, Ct)).Should().BeFalse();
        (await cache.RemoveAsync("k", Ct)).Should().BeFalse();
        (await cache.TryAddAsync("k", new Item("1"), Life, Ct)).Should().BeFalse();

        target.WriteOutcome = WriteOutcome.Unknown;
        (await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("a", new Item("1"))], Life, Ct)).Should().BeFalse();

        target.WriteOutcome = WriteOutcome.Committed;
        other.WriteOutcome = WriteOutcome.Committed;
        (await cache.SetAsync("k", new Item("1"), Life, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task On_the_plain_library_path_a_batch_call_goes_to_the_library_batch_and_not_key_by_key()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1));
        node.LibraryBackend.UsesPlainBatch = true;
        ICache<Item> cache = node.Cache;
        (await cache.GetAsync("a", Ct)).Should().BeNull();
        await cache.SetAsync("a", new Item("old"), Life, Ct);
        (await cache.GetAsync("a", Ct))!.Name.Should().Be("old");
        node.IsLocal("a").Should().BeTrue();
        var reads = node.LibraryBackend.Reads;
        var writes = node.LibraryBackend.Writes;
        var removes = node.LibraryBackend.Removes;
        var published = Bus.Published;

        (await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("a", new Item("1")), new KeyValuePair<CacheKey, Item?>("b", new Item("2")), new KeyValuePair<CacheKey, Item?>("c", new Item("3"))], Life, Ct)).Should().BeTrue();
        node.IsLocal("a").Should().BeFalse("the batch write evicts the local copy, as any write does");
        Bus.Published.Should().Be(published + 3, "and peers hear of every key");
        (await cache.GetAsync(["a", "b", "c", "d"], Ct)).Select(r => r.Value?.Name).Should().Equal("1", "2", "3", null);
        var made = await cache.GetOrAddAsync([new KeyValuePair<CacheKey, string>("d", "sd")], (states, _) => Task.FromResult(states.Select(s => new KeyValuePair<string, Item?>(s, new Item("made-" + s))).ToArray()), Ct);
        made.Single().Value!.Name.Should().Be("made-sd");
        (await cache.RemoveAsync(["a", "b"], Ct)).Should().BeTrue();

        node.LibraryBackend.BatchCalls.Should().Equal("set", "get", "getoradd", "remove");
        node.LibraryBackend.Reads.Should().Be(reads);
        node.LibraryBackend.Writes.Should().Be(writes);
        node.LibraryBackend.Removes.Should().Be(removes, "no per-key path was taken");
    }

    [Theory]
    [InlineData("source")]
    [InlineData("bypass")]
    public async Task A_batch_call_off_the_library_path_keeps_the_per_key_forms(string mode)
    {
        var (node, target, other) = Node(mode);
        node.LibraryBackend.UsesPlainBatch = true;
        ICache<Item> cache = node.Cache;

        await cache.SetAsync([new KeyValuePair<CacheKey, Item?>("a", new Item("1")), new KeyValuePair<CacheKey, Item?>("b", new Item("2"))], Life, Ct);

        other.BatchCalls.Should().BeEmpty();
        target.Writes.Should().Be(2);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_members_with_no_source_equivalent_work_on_the_library_and_are_refused_while_the_source_serves(string mode)
    {
        var (node, target, other) = Node(mode);
        ICache<Item> cache = node.Cache;
        target.ReportedTtl = Life;
        await cache.SetAsync("k", new Item("1"), Life, Ct);

        if (mode == "library")
        {
            (await cache.TryAddAsync("k", new Item("x"), Ct)).Should().BeFalse();
            (await cache.TryAddAsync("new", new Item("x"), Life, Ct)).Should().BeTrue();
            (await cache.TimeToLiveAsync("k", Ct)).Should().Be(Life);
            (await cache.ExpireTimeAsync("k", Ct)).Should().Be(Clock.GetUtcNow() + Life);
            (await cache.TimeToLiveAsync("missing", Ct)).Should().BeNull();
            (await cache.RefreshAsync("k", Life, Ct)).Should().BeTrue();
            (await cache.RefreshAsync("k", Clock.GetUtcNow().AddHours(1), Ct)).Should().BeTrue();
            target.LastRefresh.Should().Be(Clock.GetUtcNow().AddHours(1));
            (await cache.RefreshAsync("k", Ct)).Should().BeTrue();
            (await cache.TryFillAsync("k", new Item("y"), Life, Ct)).Should().Be(new FillResult<Item>(FillOutcome.Existed, new Item("1"), Clock.GetUtcNow() + Life));
            (await cache.TryFillAsync("fresh", new Item("y"), Life, Ct)).Outcome.Should().Be(FillOutcome.Added);
            target.TryAdds.Should().Be(2, "the fills are the library's own fill, not a conditional create");
            target.Refreshes.Should().Be(3);
        }
        else
        {
            var refused = new Func<Task>[]
            {
                async () => await cache.TryAddAsync("n", new Item("x"), Ct),
                async () => await cache.TryAddAsync("n", new Item("x"), Life, Ct),
                async () => await cache.TimeToLiveAsync("k", Ct),
                async () => await cache.ExpireTimeAsync("k", Ct),
                async () => await cache.RefreshAsync("k", Ct),
                async () => await cache.RefreshAsync("k", Life, Ct),
                async () => await cache.TryFillAsync("n", new Item("y"), Life, Ct),
                async () => await cache.SetIfChangedAsync("n", new Item("y"), EqualityComparer<Item>.Default, Life, Ct),
            };
            foreach (var call in refused)
            {
                await call.Should().ThrowAsync<NotSupportedException>();
            }

            (await cache.GetAsync("k", Ct))!.Name.Should().Be("1", "the source still serves everything it can");
        }

        other.TryAdds.Should().Be(0);
        other.Refreshes.Should().Be(0);
    }

    [Fact]
    public async Task A_conditional_create_that_wins_evicts_the_local_copy_and_publishes_like_any_write()
    {
        var node = NewNode(localCap: TimeSpan.FromMinutes(1));
        ICache<Item> cache = node.Cache;
        (await cache.GetAsync("k", Ct)).Should().BeNull();

        (await cache.TryAddAsync("k", new Item("claim"), Ct)).Should().BeTrue();

        node.IsLocal("k").Should().BeFalse();
        Bus.Published.Should().Be(1);
        (await cache.GetAsync("k", Ct))!.Name.Should().Be("claim");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task The_router_clears_a_routed_cache_by_name_in_every_mode_and_drops_the_local_copies(string mode)
    {
        var (node, target, _) = Node(mode);
        ICache<Item> cache = node.Cache;
        await cache.SetAsync("k", new Item("1"), Life, Ct);

        await node.Router.ClearAsync(node.CacheName, Ct);

        node.SourceBackend.Clears.Should().Be(1, "the source scans and deletes the keys, which are the library's keys too");
        (await cache.GetAsync("k", Ct)).Should().BeNull();
        target.Reads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Clearing_an_unknown_cache_name_throws_and_a_cache_the_router_does_not_hold_is_not_touched()
    {
        var node = NewNode();

        var unknown = async () => await node.Router.ClearAsync("direct-only", Ct);

        await unknown.Should().ThrowAsync<KeyNotFoundException>();
        node.SourceBackend.Clears.Should().Be(0);
    }

    [Fact]
    public async Task The_name_is_the_registration_name()
    {
        var node = NewNode();

        ((ICache<Item>)node.Cache).Name.Should().Be(node.CacheName);
        await Task.CompletedTask;
    }

    private (TestNode Node, FakeBackend Target, FakeBackend Other) Node(string mode)
    {
        var node = NewNode(useLibrary: _ => mode == "library", bypass: mode == "bypass" ? () => true : null, localCap: TimeSpan.Zero);

        // The flag is read at start: without it the first call sees the flag, begins the switch in the background and its fill is dropped by the epoch change.
        node.Router.Start();
        node.Router.Mode.Should().Be(mode == "bypass" ? RouterMode.BypassLibrary : RouterMode.Library);
        return mode == "library" ? (node, node.LibraryBackend, node.SourceBackend) : (node, node.SourceBackend, node.LibraryBackend);
    }
}
