using StackExchange.Redis;
using StackExchange.Redis.KeyspaceIsolation;
using UiPath.Caching.PhaseB;

namespace UiPath.Caching.Tests.PhaseB;

[Trait("Category", "Integration")]
public class MoveScriptsClusterTests
{
    private const string Id = "id1";
    private const string Cid = "c1";

    public static TheoryData<string> Prefixes => ["", "app:", "{shard}:"];

    public static TheoryData<bool, string, string> Cases()
    {
        var data = new TheoryData<bool, string, string>();
        foreach (var useScripts in new[] { true, false })
        {
            foreach (var library in new[] { "", "lib:" })
            {
                foreach (var prefix in new[] { "", "app:", "{shard}:" })
                {
                    data.Add(useScripts, library, prefix);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task The_computed_slot_is_the_slot_the_server_uses(string prefix)
    {
        await using var cluster = await ClusterAsync(prefix, "", useScripts: true);

        foreach (var key in InPlaceKeys())
        {
            HashSlot.Of(prefix + key).Should().Be(cluster.Mux.GetHashSlot(prefix + key), key);
            HashSlot.Of(prefix, key).Should().Be(cluster.Mux.GetHashSlot(prefix + key), key);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
        public async Task Source_scripts_run_with_the_control_record_of_the_key_slot(bool useScripts, string library, string prefix)
    {
        await using var cluster = await ClusterAsync(prefix, library, useScripts);

        foreach (var key in InPlaceKeys())
        {
            var ctl = cluster.Control(key);
            (await cluster.Scripts.ControlSyncAsync(ctl, MoveTestEnvironment.Record(1, Id, (Cid, "n1")))).Should().BeOneOf(1, 0);

            var src = cluster.Track(key);
            (await cluster.Scripts.SourceSetAsync(src, ctl, Id, "v", 60_000, onlyIfAbsent: false)).Should().Be(1, key);
            (await cluster.Scripts.CompareDeleteAsync(src, ctl, Id, "v")).Should().Be(1, key);
            (await cluster.Scripts.SourceSetAsync(src, ctl, Id, "w", -1, onlyIfAbsent: true)).Should().Be(1, key);
            (await cluster.Scripts.SourceExpireAsync(src, ctl, Id, 60_000)).Should().Be(1, key);
            (await cluster.Scripts.SourceDeleteAsync(src, ctl, Id)).Should().Be(1, key);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Reverse_promotion_captures_and_publishes_with_the_fence_of_the_in_place_key(bool useScripts, string library, string prefix)
    {
        await using var cluster = await ClusterAsync(prefix, library, useScripts);

        foreach (var key in InPlaceKeys())
        {
            var ctl = cluster.Control(key);
            await cluster.Scripts.ControlSyncAsync(ctl, MoveTestEnvironment.Record(1, Id, (Cid, "n1")));
            var keys = new FencedKeys(cluster.Track(cluster.Names.ReverseFence(key)), cluster.Track(key), ctl);

            var capture = await cluster.Scripts.CaptureAsync(keys, Id, Cid, "t0", 30_000);
            capture.Captured.Should().BeTrue(key);
            var publication = new Publication(capture.Token!, capture.Gate!, Cid, "t1", 60_000, 30_000, -1, false);

            (await cluster.Scripts.PublishAsync(keys, Id, "v", publication)).Should().Be(MoveScripts.Published, key);
            (await cluster.Db.StringGetAsync(keys.Fence)).Should().Be("t1");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Forward_promotion_captures_and_publishes_with_the_fence_of_the_tagged_native_key(bool useScripts, string library, string prefix)
    {
        await using var cluster = await ClusterAsync(prefix, library, useScripts);

        for (var i = 0; i < 100; i++)
        {
            var native = $"s:{{cache:k{i}}}";
            var ctl = cluster.Control(native);
            await cluster.Scripts.ControlSyncAsync(ctl, MoveTestEnvironment.Record(1, Id, (Cid, "n1")));
            var keys = new FencedKeys(cluster.Track(cluster.Names.ForwardFence(native)), cluster.Track(native), ctl);

            var capture = await cluster.Scripts.CaptureAsync(keys, Id, Cid, "t0", 30_000);
            capture.Captured.Should().BeTrue(native);
            var publication = new Publication(capture.Token!, capture.Gate!, Cid, "t1", 60_000, -1, -1, true);

            (await cluster.Scripts.PublishAsync(keys, Id, "v", publication)).Should().Be(MoveScripts.Published, native);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Hash_fill_and_publish_use_a_staging_key_in_the_slot_of_the_in_place_key(bool useScripts, string library, string prefix)
    {
        await using var cluster = await ClusterAsync(prefix, library, useScripts);

        foreach (var key in InPlaceKeys().Take(50))
        {
            var ctl = cluster.Control(key);
            await cluster.Scripts.ControlSyncAsync(ctl, MoveTestEnvironment.Record(1, Id, (Cid, "n1")));
            var keys = new FencedKeys(cluster.Track(cluster.Names.ReverseFence(key)), cluster.Track(key), ctl);
            var staging = cluster.Track(cluster.Names.ReverseStaging(key, MoveScripts.NewNonce()));
            await cluster.Db.HashSetAsync(staging, [new HashEntry("a", "1")]);

            (await cluster.Scripts.FillHashAsync(keys, staging, Id, 30_000, "f1", 60_000)).Should().Be(1, key);
            (await cluster.Db.HashGetAsync(key, "a")).Should().Be("1");

            var again = cluster.Track(cluster.Names.ReverseStaging(key, MoveScripts.NewNonce()));
            await cluster.Db.HashSetAsync(again, [new HashEntry("a", "2")]);
            await cluster.Db.KeyDeleteAsync(key);
            var capture = await cluster.Scripts.CaptureAsync(keys, Id, Cid, "t0", 30_000);
            var publication = new Publication(capture.Token!, capture.Gate!, Cid, "t1", 60_000, -1, -1, false);

            (await cluster.Scripts.PublishHashAsync(keys, again, Id, publication)).Should().Be(MoveScripts.Published, key);
            (await cluster.Db.HashGetAsync(key, "a")).Should().Be("2");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_tagged_prefix_puts_every_control_record_on_one_slot(bool useScripts)
    {
        await using var cluster = await ClusterAsync("{shard}:", "lib:", useScripts);

        cluster.Names.SlotSet().Should().ContainSingle().Which.Should().Be(HashSlot.Of("{shard}:"));
        var records = new HashSet<string>();
        foreach (var key in InPlaceKeys())
        {
            var ctl = cluster.Control(key);
            records.Add(ctl.ToString());
            await cluster.Scripts.ControlSyncAsync(ctl, MoveTestEnvironment.Record(1, Id, (Cid, "n1")));
        }

        records.Should().ContainSingle();
        (await cluster.Db.HashGetAsync(cluster.Control(InPlaceKeys().First()), "id")).Should().Be(Id);
    }

    private static IEnumerable<string> InPlaceKeys() =>
        Enumerable.Range(0, 100).Select(i => $"n:cache{i % 7},c:key{i}");

    private static async Task<ClusterContext> ClusterAsync(string prefix, string library, bool useScripts)
    {
        var connectionString = Environment.GetEnvironmentVariable("REDIS_CLUSTER_CONNECTION_STRING");
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(connectionString), "Set REDIS_CLUSTER_CONNECTION_STRING to a Redis Cluster to run.");
        var mux = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        var db = mux.GetDatabase().WithKeyPrefix(prefix);
        var version = mux.GetServers().Min(s => s.Version)!;
        return new ClusterContext(mux, db, new MoveScripts(db, useScripts, version), new MoveKeyNames(prefix, library, cluster: true), Guid.NewGuid().ToString("N"));
    }

    private sealed class ClusterContext(ConnectionMultiplexer mux, IDatabase db, MoveScripts scripts, MoveKeyNames names, string group) : IAsyncDisposable
    {
        private readonly List<RedisKey> _made = [];

        public ConnectionMultiplexer Mux => mux;

        public IDatabase Db => db;

        public MoveScripts Scripts => scripts;

        public MoveKeyNames Names => names;

        public RedisKey Control(string dataKey) => Track(names.ControlFor(group, dataKey));

        public RedisKey Track(RedisKey key)
        {
            _made.Add(key);
            return key;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                foreach (var key in _made.Distinct())
                {
                    await db.KeyDeleteAsync(key);
                }
            }
            finally
            {
                await mux.DisposeAsync();
            }
        }
    }
}
