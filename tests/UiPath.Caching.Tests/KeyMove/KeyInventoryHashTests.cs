using System.Collections.Concurrent;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The inventory of a hash cache judges a key by its field count, asked of the server and never read, in the same batches as the lives, and a droppable hash does not keep the drain from passing.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class KeyInventoryHashTests(RedisContainerFixture fixture)
{
    private const int Cap = 3;

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task A_hash_cache_is_judged_by_HLEN_in_one_batch_with_a_string_cache_judged_by_STRLEN_and_no_value_is_ever_read()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Space.Kinds["b"] = KeyMoveKind.Hash;
        var calls = new ConcurrentQueue<(string Method, string Key)>();
        var spy = InterceptingProxy.Wrap(kit.Kit.Db, (method, args) =>
        {
            calls.Enqueue((method.Name, args.OfType<RedisKey>().FirstOrDefault().ToString()));
            return InterceptingProxy.Proceed;
        });
        var seen = new ConcurrentBag<(string Cache, long Length)>();
        var inventory = new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, spy, kit.Prefix, kit.Kit.Time, values: new Cap3(seen, noOrigin: false));
        var keys = new List<string>
        {
            await kit.SetAsync(MoveSide.Legacy, "a", "mv-string", value: "12345"),
            await SetHashAsync(kit, "b", "mv-at", Cap),
            await SetHashAsync(kit, "b", "mv-over", Cap + 1),
        };

        var result = (await inventory.RunAsync(kit.Group, Ct))!;

        result.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["b"] = 2 });
        result.Droppable.Should().Be(1, "only the hash above the cap");
        result.Movable.Should().Be(2);
        seen.Should().BeEquivalentTo([("a", 5L), ("b", (long)Cap), ("b", (long)Cap + 1)]);
        calls.Where(c => c.Method == "HashLengthAsync").Select(c => c.Key).Should().BeEquivalentTo(keys.Where(k => k.Contains(":b:", StringComparison.Ordinal)).Select(k => kit.Prefix + k));
        calls.Where(c => c.Method == "StringLengthAsync").Select(c => c.Key).Should().ContainSingle().Which.Should().Be(kit.Prefix + keys[0]);
        calls.Select(c => c.Method).Should().NotContain(m => m.StartsWith("HashGet", StringComparison.Ordinal) || m.StartsWith("StringGet", StringComparison.Ordinal) || m.StartsWith("HashScan", StringComparison.Ordinal) || m.StartsWith("HashValues", StringComparison.Ordinal) || m.StartsWith("HashKeys", StringComparison.Ordinal), "a hash is never read to be classified");
        await DeleteAsync(kit, keys);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(Cap, false)]
    [InlineData(Cap + 1, true)]
    [InlineData(Cap + 50, true)]
    public async Task A_hash_is_droppable_above_the_cap_and_not_at_it(int fields, bool droppable)
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Space.Kinds["b"] = KeyMoveKind.Hash;
        var inventory = new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, kit.Kit.Db, kit.Prefix, kit.Kit.Time, values: new Cap3([], noOrigin: false));
        var key = await SetHashAsync(kit, "b", "mv", fields);

        var result = (await inventory.RunAsync(kit.Group, Ct))!;

        result.Droppable.Should().Be(droppable ? 1 : 0);
        result.Movable.Should().Be(droppable ? 0 : 1);
        result.Residue["b"].Should().Be(1, "a droppable key is still the residue");
        await DeleteAsync(kit, [key]);
    }

    [Fact]
    public async Task The_drain_passes_with_only_droppable_hashes_left_and_a_hash_that_the_cache_cannot_drop_blocks_it()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Space.Kinds["b"] = KeyMoveKind.Hash;
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, "1"), (StateFields.AllAck, state.Id)], []);
        var key = await SetHashAsync(kit, "b", "mv-over", Cap + 2);
        var origin = new InventoryMoverStatus(new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, kit.Kit.Db, kit.Prefix, kit.Kit.Time, values: new Cap3([], noOrigin: false)), kit.Kit.Time, (_, _, _) => Task.FromResult(true));
        var noOrigin = new InventoryMoverStatus(new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, kit.Kit.Db, kit.Prefix, kit.Kit.Time, values: new Cap3([], noOrigin: true)), kit.Kit.Time, (_, _, _) => Task.FromResult(true));

        (await origin.DrainAsync(kit.Group, "fwd", Ct))!.Should().Match<DrainReport>(r => r.Passed && r.Residue["b"] == 1);
        (await noOrigin.DrainAsync(kit.Group, "fwd", Ct))!.Passed.Should().BeFalse("without an origin the hash is the only copy: it stays, counts as movable and blocks DRAINED");
        await DeleteAsync(kit, [key]);
    }

    [Fact]
    public async Task A_key_of_another_type_in_a_hash_cache_is_judged_as_movable_and_a_cache_with_no_classifier_asks_for_no_length()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Space.Kinds["b"] = KeyMoveKind.Hash;
        var wrong = await kit.SetAsync(MoveSide.Legacy, "b", "mv-string", value: "x");
        var calls = new ConcurrentQueue<string>();
        var spy = InterceptingProxy.Wrap(kit.Kit.Db, (method, _) =>
        {
            calls.Enqueue(method.Name);
            return InterceptingProxy.Proceed;
        });

        var judged = (await new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, spy, kit.Prefix, kit.Kit.Time, values: new Cap3([], noOrigin: false)).RunAsync(kit.Group, Ct))!;
        calls.Clear();
        var unjudged = (await new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, spy, kit.Prefix, kit.Kit.Time).RunAsync(kit.Group, Ct))!;

        judged.Movable.Should().Be(1);
        unjudged.Movable.Should().Be(1);
        calls.Should().NotContain(c => c.StartsWith("HashLength", StringComparison.Ordinal) || c.StartsWith("StringLength", StringComparison.Ordinal));
        _ = wrong;
    }

    private static async Task<string> SetHashAsync(DataOpsKit kit, string cache, string name, int fields)
    {
        var key = $"{kit.Space.PrefixOf(MoveSide.Legacy)}{cache}:{name}";
        await kit.Kit.Db.HashSetAsync(kit.Prefix + key, [.. Enumerable.Range(0, fields).Select(i => new HashEntry($"f{i}", "v"))]);
        return key;
    }

    private static async Task DeleteAsync(DataOpsKit kit, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            await kit.Kit.Db.KeyDeleteAsync(kit.Prefix + key);
        }
    }

    // A hash is droppable above the field cap when its cache has an origin; a string cache is judged by five bytes.
    private sealed class Cap3(ConcurrentBag<(string Cache, long Length)> seen, bool noOrigin) : IValueClassifier
    {
        public bool IsDroppable(string cacheId, MoveSide side, long length)
        {
            seen.Add((cacheId, length));
            return cacheId == "b" && length > Cap && !noOrigin;
        }
    }
}
