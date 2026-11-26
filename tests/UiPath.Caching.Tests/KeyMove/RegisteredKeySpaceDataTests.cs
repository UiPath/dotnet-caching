using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The registered key map under the scans: the pattern starts with the connector prefix, the prefix is stripped before the key map and the predicate run, and only managed caches' keys are purged and counted.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class RegisteredKeySpaceDataTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData("")]
    [InlineData("app:")]
    public async Task The_purge_and_the_inventory_follow_the_key_map_under_a_connector_prefix(string prefix)
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true, prefix: prefix);
        await Run(kit, prefix);
    }

    [Fact]
    public async Task The_same_holds_on_the_cluster_under_a_prefix()
    {
        await using var kit = await StateKit.ClusterAsync(true, prefix: "app:");
        await Run(kit, "app:");
    }

    [Fact]
    public async Task Overlapping_scan_patterns_count_a_key_once()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var tag = "km-registered" + Guid.NewGuid().ToString("N")[..10];
        var wide = Cache(tag, "a", kit.Group) with { NativePattern = $"{tag}:n:*", OwnsNative = k => k.StartsWith($"{tag}:n:a:", StringComparison.Ordinal) };
        var narrow = Cache(tag, "b", kit.Group);
        var (data, _, made) = await Prepare(kit, new RegisteredKeySpace([wide, narrow], tag + ":n:"), [$"{tag}:n:a:k1", $"{tag}:n:b:k2", $"{tag}:n:b:k3"]);
        try
        {
            (await data.CountNativeAsync(kit.Group, CancellationToken.None)).Should().Be(3, "the b keys match both patterns and are counted once");
        }
        finally
        {
            await Task.WhenAll(made.Select(k => kit.Db.KeyDeleteAsync(k)));
        }
    }

    [Fact]
    public async Task A_key_two_caches_own_is_refused_by_the_purge_and_counted_by_the_inventory_and_alerted_and_never_dropped()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var tag = "km-registered" + Guid.NewGuid().ToString("N")[..10];
        var both = $"{tag}:n:x:shared";
        var a = Cache(tag, "a", kit.Group) with { NativePattern = $"{tag}:n:*", OwnsNative = k => k.StartsWith($"{tag}:n:a:", StringComparison.Ordinal) || k == both };
        var b = Cache(tag, "b", kit.Group) with { NativePattern = $"{tag}:n:*", OwnsNative = k => k.StartsWith($"{tag}:n:b:", StringComparison.Ordinal) || k == both };
        var alerts = new List<KeyMoveAlert>();
        var (data, inventory, made) = await Prepare(kit, new RegisteredKeySpace([a, b], tag + ":n:"), [$"{tag}:n:a:k1", $"{tag}:n:b:k2", both], alerts.Add);
        try
        {
            (await data.CountNativeAsync(kit.Group, CancellationToken.None)).Should().Be(3, "the shared key counts as present");
            var found = (await inventory.RunAsync(kit.Group, CancellationToken.None))!;
            found.Ambiguous.Should().Be(0, "the inventory scans the in-place side, where the key does not exist");
            var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
            var act = () => data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), CancellationToken.None);

            await act.Should().ThrowAsync<PurgeRefusedException>().WithMessage("*shared*a and b*");

            (await kit.Db.KeyExistsAsync(both)).Should().BeTrue("the shared key is left in place");
            (await kit.Db.KeyExistsAsync($"{tag}:n:a:k1")).Should().BeFalse("the others are purged");
            alerts.Should().Contain(x => x.Code == "ambiguous_ownership" && x.Detail == $"{both}: a,b" && x.Group == kit.Group);
        }
        finally
        {
            await Task.WhenAll(made.Select(k => kit.Db.KeyDeleteAsync(k)));
        }
    }

    [Fact]
    public async Task The_inventory_counts_an_ambiguous_key_for_every_claiming_cache_and_gives_no_drain_evidence()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var tag = "km-registered" + Guid.NewGuid().ToString("N")[..10];
        var both = $"{tag}:l:x:shared";
        var a = Cache(tag, "a", kit.Group) with { InPlacePattern = $"{tag}:l:*", OwnsInPlace = k => k.StartsWith($"{tag}:l:a:", StringComparison.Ordinal) || k == both };
        var b = Cache(tag, "b", kit.Group) with { InPlacePattern = $"{tag}:l:*", OwnsInPlace = k => k.StartsWith($"{tag}:l:b:", StringComparison.Ordinal) || k == both };
        var alerts = new List<KeyMoveAlert>();
        var (_, inventory, made) = await Prepare(kit, new RegisteredKeySpace([a, b], tag + ":n:"), [$"{tag}:l:a:k1", both], alerts.Add);
        try
        {
            var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
            await kit.Scripts.FieldsAsync(kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
            state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;

            var found = (await inventory.RunAsync(kit.Group, CancellationToken.None))!;

            found.Ambiguous.Should().Be(1);
            found.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 2, ["b"] = 1 });
            found.Movable.Should().Be(1, "only the unambiguous key can move");
            (await new InventoryMoverStatus(inventory, kit.Time, (_, _, _) => Task.FromResult(true)).DrainAsync(kit.Group, state, CancellationToken.None)).Should().BeNull();
            alerts.Should().Contain(x => x.Code == "ambiguous_ownership");
        }
        finally
        {
            await Task.WhenAll(made.Select(k => kit.Db.KeyDeleteAsync(k)));
        }
    }

    [Fact]
    public async Task A_key_an_unmanaged_cache_also_claims_is_ambiguous_for_the_purge_and_the_inventory()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var tag = "km-registered" + Guid.NewGuid().ToString("N")[..10];
        var nativeShared = $"{tag}:n:x:shared";
        var legacyShared = $"{tag}:l:x:shared";
        KeyMoveCache Claimant(string name) => Cache(tag, name, kit.Group) with
        {
            NativePattern = $"{tag}:n:*",
            InPlacePattern = $"{tag}:l:*",
            OwnsNative = k => k.StartsWith($"{tag}:n:{name}:", StringComparison.Ordinal) || k == nativeShared,
            OwnsInPlace = k => k.StartsWith($"{tag}:l:{name}:", StringComparison.Ordinal) || k == legacyShared,
        };
        var alerts = new List<KeyMoveAlert>();

        // Cache c is registered but not in the state's managed set (a and b).
        var (data, inventory, made) = await Prepare(kit, new RegisteredKeySpace([Claimant("a"), Claimant("c")], tag + ":n:"), [$"{tag}:n:a:k1", nativeShared, legacyShared, $"{tag}:n:c:only-unmanaged"], alerts.Add);
        try
        {
            (await data.CountNativeAsync(kit.Group, CancellationToken.None)).Should().Be(2, "a's own key and the shared one; c's own key is not ours");
            var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
            var act = () => data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), CancellationToken.None);

            await act.Should().ThrowAsync<PurgeRefusedException>().WithMessage("*shared*a and c*");

            (await kit.Db.KeyExistsAsync(nativeShared)).Should().BeTrue("a key the unmanaged cache also claims is left in place");
            (await kit.Db.KeyExistsAsync($"{tag}:n:c:only-unmanaged")).Should().BeTrue("an unmanaged cache's own key is never touched");
            (await kit.Db.KeyExistsAsync($"{tag}:n:a:k1")).Should().BeFalse();
            var found = (await inventory.RunAsync(kit.Group, CancellationToken.None))!;
            found.Ambiguous.Should().Be(1);
            found.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["b"] = 0 }, "the managed claimant carries the ambiguous key; c is not a managed cache");
            alerts.Should().Contain(x => x.Code == "ambiguous_ownership" && x.Detail.EndsWith("a,c", StringComparison.Ordinal));
        }
        finally
        {
            await Task.WhenAll(made.Select(k => kit.Db.KeyDeleteAsync(k)));
        }
    }

    [Fact]
    public async Task A_broad_cache_cannot_purge_or_count_the_keys_of_a_cache_of_another_group()
    {
        await using var kit = await StateKit.StandaloneAsync(fixture, true);
        var tag = "km-registered" + Guid.NewGuid().ToString("N")[..10];
        var broad = Cache(tag, "a", kit.Group) with
        {
            NativePattern = $"{tag}:n:*",
            InPlacePattern = $"{tag}:l:*",
            OwnsNative = k => k.StartsWith($"{tag}:n:", StringComparison.Ordinal),
            OwnsInPlace = k => k.StartsWith($"{tag}:l:", StringComparison.Ordinal),
        };
        var foreign = Cache(tag, "other-b", "another-group");
        var own = $"{tag}:n:a:k1";
        var theirs = $"{tag}:n:other-b:k2";
        var theirsLegacy = $"{tag}:l:other-b:k3";
        var alerts = new List<KeyMoveAlert>();
        var (data, inventory, made) = await Prepare(kit, new RegisteredKeySpace([broad, foreign], tag + ":n:"), [own, theirs, theirsLegacy, $"{tag}:l:a:k4"], alerts.Add);
        try
        {
            (await data.CountNativeAsync(kit.Group, CancellationToken.None)).Should().Be(2, "a's key and the one the other group's cache also owns");
            var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
            var act = () => data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), CancellationToken.None);

            await act.Should().ThrowAsync<PurgeRefusedException>().WithMessage("*other-b*");

            (await kit.Db.KeyExistsAsync(theirs)).Should().BeTrue("another group's key is never deleted");
            (await kit.Db.KeyExistsAsync(own)).Should().BeFalse();
            await kit.Scripts.FieldsAsync(kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
            state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
            var found = (await inventory.RunAsync(kit.Group, CancellationToken.None))!;
            found.Ambiguous.Should().Be(1);
            found.Residue["a"].Should().Be(2);
            alerts.Should().Contain(x => x.Code == "ambiguous_ownership" && x.Detail.Contains("other-b", StringComparison.Ordinal));
        }
        finally
        {
            await Task.WhenAll(made.Select(k => kit.Db.KeyDeleteAsync(k)));
        }
    }

    private static async Task<(GroupDataOps Data, KeyInventory Inventory, List<string> Made)> Prepare(StateKit kit, RegisteredKeySpace space, string[] keys, Action<KeyMoveAlert>? alert = null)
    {
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, string.Empty, TimeProvider.System, TimeSpan.Zero);
        var scripts = new MoveScripts(kit.ControlDb, true, kit.Mux.GetServers().Min(s => s.Version)!);
        foreach (var key in keys)
        {
            await kit.Db.StringSetAsync(key, "v");
        }

        return (new GroupDataOps(scanner, space, kit.Store, kit.Scripts, scripts, kit.Names, alert: alert), new KeyInventory(scanner, space, kit.Store, kit.Db, string.Empty, kit.Time, alert), [.. keys]);
    }

    private static async Task Run(StateKit kit, string prefix)
    {
        var tag = "km-registered" + Guid.NewGuid().ToString("N")[..10];
        var space = new RegisteredKeySpace([Cache(tag, "a", kit.Group), Cache(tag, "b", kit.Group), Cache(tag, "c", kit.Group)], tag + ":n:");
        var init = await kit.InitAsync("a", "b");
        (await kit.Sequence.RunAsync(kit.Group, StateKit.Expecting(init), StateKit.TargetFrom(init, "S1", "fwd", 0), null, CancellationToken.None)).Outcome.Should().Be(TransitionOutcome.Completed);
        var scanner = new MasterScanner(() => kit.Mux.GetServers(), kit.Topology, 0, prefix, TimeProvider.System, TimeSpan.Zero);
        var scripts = new MoveScripts(kit.ControlDb, true, kit.Mux.GetServers().Min(s => s.Version)!);
        var data = new GroupDataOps(scanner, space, kit.Store, kit.Scripts, scripts, kit.Names);
        var inventory = new KeyInventory(scanner, space, kit.Store, kit.Db, prefix, kit.Time);
        var made = new List<string>();
        async Task Set(string key)
        {
            made.Add(prefix + key);
            await kit.Db.StringSetAsync(prefix + key, "v");
        }

        try
        {
            await Set($"{tag}:n:a:k1");
            await Set($"{tag}:n:b:k2");
            await Set($"{tag}:n:c:k3");
            await Set($"{tag}:l:a:k4");
            await Set($"{tag}:l:b:k5");
            await Set($"{tag}:l:c:k6");
            if (prefix.Length > 0)
            {
                kit.Db.StringSet($"{tag}:n:a:bare", "v");
                made.Add($"{tag}:n:a:bare");
            }

            await AssertManagedPurgeAsync(kit, tag, prefix, data, inventory);
        }
        finally
        {
            await Task.WhenAll(made.Select(k => kit.Db.KeyDeleteAsync(k)));
        }
    }

    private static async Task AssertManagedPurgeAsync(StateKit kit, string tag, string prefix, GroupDataOps data, KeyInventory inventory)
    {
        (await data.CountNativeAsync(kit.Group, CancellationToken.None)).Should().Be(2, "the stripped key reaches the predicate; c is not managed and the unprefixed key is not found");
        var found = (await inventory.RunAsync(kit.Group, CancellationToken.None))!;
        found.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["b"] = 1 });
        found.Movable.Should().Be(2);

        var state = (await kit.Store.ReadAsync(kit.Group, CancellationToken.None))!;
        await data.PurgeNativeAsync(kit.Group, new DataGuard(state.Id, null), CancellationToken.None);

        (await kit.Db.KeyExistsAsync(prefix + $"{tag}:n:a:k1")).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(prefix + $"{tag}:n:b:k2")).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(prefix + $"{tag}:n:c:k3")).Should().BeTrue();
        (await kit.Db.KeyExistsAsync(prefix + $"{tag}:l:a:k4")).Should().BeTrue();
        (await kit.Db.KeyExistsAsync($"{tag}:n:a:bare")).Should().Be(prefix.Length > 0);
        await data.PurgeLegacyAsync(kit.Group, new DataGuard(state.Id, null), CancellationToken.None);
        (await kit.Db.KeyExistsAsync(prefix + $"{tag}:l:b:k5")).Should().BeFalse();
        (await kit.Db.KeyExistsAsync(prefix + $"{tag}:l:c:k6")).Should().BeTrue();
    }

    private static KeyMoveCache Cache(string tag, string name, string group) =>
        new(name, group, false, false, false, null, $"{tag}:l:{name}:*", k => $"{tag}:l:{name}:{k}", k => Strip(k, $"{tag}:l:{name}:"), $"{tag}:n:{name}:*", k => $"{tag}:n:{name}:{k}", k => Strip(k, $"{tag}:n:{name}:"));

    private static string? Strip(string key, string head) => key.StartsWith(head, StringComparison.Ordinal) ? key[head.Length..] : null;
}
