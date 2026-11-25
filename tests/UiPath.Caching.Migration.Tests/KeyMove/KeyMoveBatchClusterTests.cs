using System.Collections.Concurrent;
using System.Net;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Batches on a cluster: keys of many slots, each promoted and written under the fence of its own slot, and a shard that fails takes only its own keys with it.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class KeyMoveBatchClusterTests(RedisFixture fixture)
{
    private static readonly string[] Keys = [.. Enumerable.Range(0, 24).Select(i => "k" + i)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_batch_read_serves_N_promotes_L_under_each_keys_own_fence_and_misses_the_rest_across_the_slots()
    {
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true);
        await kit.PrepareAsync(Keys);
        await kit.SetStateAsync("S3");
        var (inN, inL, absent) = (Keys[..8], Keys[8..16], Keys[16..]);
        foreach (var key in inN)
        {
            await kit.Db.StringSetAsync(kit.N(key), "n-" + key);
        }

        foreach (var key in inL)
        {
            await kit.Db.StringSetAsync(kit.L(key), "l-" + key);
        }

        SlotsOf(kit, Keys).Count.Should().BeGreaterThan(1, "the keys spread over the masters");

        var read = await kit.Client.ReadManyAsync(Keys, Ct);

        foreach (var key in inN)
        {
            read[key].Value!.Name.Should().Be("n-" + key);
        }

        foreach (var key in inL)
        {
            read[key].Value!.Name.Should().Be("l-" + key);
            (await kit.GetAsync(kit.N(key))).Should().Be("l-" + key, $"{key} was promoted");
            (await kit.ExistsAsync(kit.L(key))).Should().BeFalse();
            (await kit.FenceAsync(key)).Should().NotBeNull($"the fence of {key} sits in the slot of {key}");
        }

        foreach (var key in absent)
        {
            read[key].Kind.Should().Be(ReadOutcome.Absent);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_batch_write_and_a_batch_delete_run_every_key_in_order_in_its_own_slot(bool scripts)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var commands = new ConcurrentQueue<RedisKey>();
        await using var kit = await KeyMoveKit.CreateAsync(fixture, scripts, cluster: true, clock: clock, interceptDb: db => RecordWrites(db, commands));
        await kit.PrepareAsync(Keys);
        await kit.SetStateAsync("S3");
        foreach (var key in Keys)
        {
            await kit.Db.StringSetAsync(kit.L(key), "old");
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        var written = await kit.Client.SetManyAsync([.. Keys.Select(k => (k, new Item("new-" + k), (DateTimeOffset?)null))], Ct);

        written.Should().AllSatisfy(w => w.Outcome.Should().Be(WriteOutcome.Committed));
        foreach (var key in Keys)
        {
            AssertWriteOrder(kit, key, commands);
            (await kit.GetAsync(kit.N(key))).Should().Be("new-" + key);
            (await kit.ExistsAsync(kit.L(key))).Should().BeFalse();
        }

        commands.Clear();
        var removed = await kit.Client.RemoveManyAsync(Keys[..12], Ct);

        removed.Should().AllSatisfy(r => r.Outcome.Should().Be(WriteOutcome.Committed));
        foreach (var key in Keys[..12])
        {
            AssertWriteOrder(kit, key, commands);
            (await kit.ExistsAsync(kit.N(key))).Should().BeFalse();
        }

        (await kit.GetAsync(kit.N(Keys[12]))).Should().Be("new-" + Keys[12]);
    }

    [Fact]
    public async Task A_shard_that_fails_types_only_its_own_keys_of_a_batch_write_and_the_others_commit()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        ShardFailingDatabase? failing = null;
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true, clock: clock, interceptDb: db => ShardFailingDatabase.Over(db, out failing));
        var down = ShardFailingDatabase.OwnerOf(kit.Mux, kit.L(Keys[0]))!;
        var keys = Keys.Append(KeyWithOnlyDestinationDown(kit, down)).ToArray();
        await kit.PrepareAsync(keys);
        await kit.SetStateAsync("S3");
        foreach (var key in keys)
        {
            await kit.Db.StringSetAsync(kit.L(key), "old");
        }

        failing!.Failing = down;
        clock.Advance(TimeSpan.FromSeconds(1));
        var written = await kit.Client.SetManyAsync([.. keys.Select(k => (k, new Item("new-" + k), (DateTimeOffset?)null))], Ct);

        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (Equals(ShardFailingDatabase.OwnerOf(kit.Mux, kit.L(key)), down))
            {
                written[i].Outcome.Should().NotBe(WriteOutcome.Committed, $"the shard of {key} is down");
                (await kit.GetAsync(kit.L(key))).Should().Be("old", $"nothing of {key} was written");
            }
            else if (Equals(ShardFailingDatabase.OwnerOf(kit.Mux, kit.N(key)), down))
            {
                written[i].Outcome.Should().NotBe(WriteOutcome.Committed, $"the destination shard of {key} is down");
                (await kit.ExistsAsync(kit.N(key))).Should().BeFalse();
                (await kit.ExistsAsync(kit.L(key))).Should().BeFalse($"the source delete of {key} ran first");
            }
            else
            {
                written[i].Outcome.Should().Be(WriteOutcome.Committed, $"{key} lives on shards that are up");
                (await kit.GetAsync(kit.N(key))).Should().Be("new-" + key);
            }
        }

        written.Count(w => w.Outcome == WriteOutcome.Committed).Should().BeGreaterThan(0);
        written.Count(w => w.Outcome != WriteOutcome.Committed).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_shard_that_fails_makes_only_its_own_keys_of_a_batch_read_unavailable()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        ShardFailingDatabase? failing = null;
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true, clock: clock, interceptDb: db => FailMultiGet(db, out failing));
        await kit.PrepareAsync(Keys);
        await kit.SetStateAsync("S3");
        foreach (var key in Keys)
        {
            await kit.Db.StringSetAsync(kit.N(key), "n-" + key);
        }

        var down = ShardFailingDatabase.OwnerOf(kit.Mux, kit.N(Keys[0]))!;
        failing!.Failing = down;
        clock.Advance(TimeSpan.FromSeconds(1));
        var read = await kit.Client.ReadManyAsync(Keys, Ct);

        foreach (var key in Keys)
        {
            if (Equals(ShardFailingDatabase.OwnerOf(kit.Mux, kit.N(key)), down))
            {
                read[key].Kind.Should().Be(ReadOutcome.Unavailable, $"the shard of {key} is down");
            }
            else
            {
                read[key].Kind.Should().Be(ReadOutcome.Found, $"{key} lives on a shard that is up");
                read[key].Value!.Name.Should().Be("n-" + key);
            }
        }
    }

    // A hit whose life could not be read is never served as a key that does not expire.
    [Fact]
    public async Task A_hit_whose_life_cannot_be_read_in_the_batch_is_never_served_as_one_that_does_not_expire()
    {
        ShardFailingDatabase? failing = null;
        await using var kit = await KeyMoveKit.CreateAsync(fixture, cluster: true, interceptDb: db => ShardFailingDatabase.Over(db, out failing));
        await kit.PrepareAsync(Keys);
        await kit.SetStateAsync("S3");
        foreach (var key in Keys)
        {
            await kit.Db.StringSetAsync(kit.N(key), "n-" + key, TimeSpan.FromMinutes(10));
        }

        var down = ShardFailingDatabase.OwnerOf(kit.Mux, kit.N(Keys[0]))!;
        failing!.OnlyCommand = "PTTL";
        failing.Failing = down;
        var read = await kit.Client.ReadManyAsync(Keys, Ct);

        foreach (var key in Keys)
        {
            // A key whose batch life read failed is unavailable or read again on its own with its life; it is never served as one that does not expire.
            if (read[key].Kind == ReadOutcome.Unavailable)
            {
                Equals(ShardFailingDatabase.OwnerOf(kit.Mux, kit.N(key)), down).Should().BeTrue($"only the keys of the failed shard are unavailable, not {key}");
                continue;
            }

            read[key].Value!.Name.Should().Be("n-" + key);
            read[key].Expiration.Should().NotBeNull($"{key} has a life").And.BeAfter(DateTimeOffset.UtcNow.AddMinutes(9));
        }
    }

    private static HashSet<int> SlotsOf(KeyMoveKit kit, IEnumerable<string> keys) =>
        [.. keys.SelectMany(k => new[] { kit.Mux.HashSlot(kit.L(k)), kit.Mux.HashSlot(kit.N(k)) })];

    private static string KeyWithOnlyDestinationDown(KeyMoveKit kit, EndPoint down)
    {
        var layout = kit.Mux.GetServers().First(server => server.IsConnected).ClusterConfiguration!;
        var up = layout.Nodes.First(node => !node.IsReplica && node.Slots.Count > 0 && !Equals(node.EndPoint, down));
        var tag = SlotTags.For(up.Slots[0].From);

        // L hashes the explicit tag. N includes the varying prefix in its own tag.
        return Enumerable.Range(0, HashSlot.Count)
            .Select(i => $"{i}{{{tag}}}")
            .First(key => Equals(ShardFailingDatabase.OwnerOf(kit.Mux, kit.N(key)), down));
    }

    private static IDatabase FailMultiGet(IDatabase db, out ShardFailingDatabase failing)
    {
        var shard = ShardFailingDatabase.Over(db, out failing);
        return InterceptingProxy.Wrap(db, (method, args) => method.Name == nameof(IDatabase.StringGetAsync) && args[0] is RedisKey[] keys
            ? shard.StringGetAsync(keys, (CommandFlags)args[1]!)
            : InterceptingProxy.Proceed);
    }

    private static void AssertWriteOrder(KeyMoveKit kit, string key, ConcurrentQueue<RedisKey> commands)
    {
        RedisKey source = kit.L(key);
        RedisKey destination = kit.N(key);
        commands.Where(command => command == source || command == destination).Should().Equal(source, destination);
    }

    private static IDatabase RecordWrites(IDatabase db, ConcurrentQueue<RedisKey> commands) =>
        InterceptingProxy.Wrap(db, (method, args) =>
        {
            if (method.Name == nameof(IDatabase.CreateTransaction))
            {
                return RecordTransaction(db.CreateTransaction(args[0]), commands);
            }

            if (method.Name == nameof(IDatabase.ScriptEvaluateAsync) && args[1] is RedisKey[] keys)
            {
                // SRCDEL takes the source first. BUMP takes the fence first and the destination second.
                commands.Enqueue(keys.Length == 2 ? keys[0] : keys[1]);
            }

            return InterceptingProxy.Proceed;
        });

    private static ITransaction RecordTransaction(ITransaction transaction, ConcurrentQueue<RedisKey> commands)
    {
        var writes = new List<RedisKey>();
        return InterceptingProxy.Wrap(transaction, (method, args) =>
        {
            if (method.Name is nameof(IDatabase.KeyDeleteAsync) or nameof(IDatabase.StringSetAsync))
            {
                writes.Add((RedisKey)args[0]!);
            }

            if (method.Name == nameof(ITransaction.ExecuteAsync))
            {
                foreach (var key in writes)
                {
                    commands.Enqueue(key);
                }
            }

            return InterceptingProxy.Proceed;
        });
    }
}
