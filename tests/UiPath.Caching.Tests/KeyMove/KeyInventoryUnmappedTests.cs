using System.Diagnostics.Metrics;
using System.Globalization;
using NSubstitute;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class KeyInventoryUnmappedTests(RedisContainerFixture fixture)
{
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Only_ownerless_keys_count_as_unmapped_and_they_never_block_the_drain()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Kit.Time.Advance(TimeSpan.FromDays(1));
        await SetAckedAsync(kit);
        await kit.SetAsync(MoveSide.Legacy, "a", "k1");
        await kit.SetAsync(MoveSide.Legacy, "c", "outside");
        await SetOwnersAsync(kit, "outside-pair", "c", "d");
        await SetOwnersAsync(kit, "unmapped");
        var status = new InventoryMoverStatus(kit.Inventory, kit.Kit.Time, (_, _, _) => Task.FromResult(true));

        var inventory = (await kit.Inventory.RunAsync(kit.Group, Ct))!;
        var report = (await status.DrainAsync(kit.Group, await kit.StateAsync(), Ct))!;

        inventory.Should().Match<InventoryResult>(r => r.ExpectedUnmapped == 1 && r.Movable == 0 && r.Ambiguous == 0);
        report.Should().Match<DrainReport>(r => r.Passed && r.ExpectedUnmapped == 1);
        inventory.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["b"] = 0 });
    }

    [Fact]
    public async Task An_outside_owner_overlapping_a_managed_owner_makes_the_key_ambiguous()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        await SetOwnersAsync(kit, "overlap", "a", "c");

        var inventory = (await kit.Inventory.RunAsync(kit.Group, Ct))!;

        inventory.Should().Match<InventoryResult>(r => r.ExpectedUnmapped == 0 && r.Movable == 0 && r.Ambiguous == 1);
        inventory.Residue.Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["b"] = 0 });
    }

    [Fact]
    public async Task The_gauges_hold_the_values_of_the_last_inventory_of_the_group()
    {
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Kit.Time.Advance(TimeSpan.FromDays(1));
        kit.Space.MaxTtl["a"] = TimeSpan.FromMinutes(10);
        await SetAckedAsync(kit);
        await kit.SetAsync(MoveSide.Legacy, "a", "long", TimeSpan.FromHours(1));
        await SetOwnersAsync(kit, "k2");
        using var gauges = new GaugeReader(kit.Group);

        await kit.Inventory.RunAsync(kit.Group, Ct);
        var first = gauges.Read();
        await SetOwnersAsync(kit, "k3");
        await kit.SetAsync(MoveSide.Legacy, "a", "long2", TimeSpan.FromHours(2));
        await kit.Inventory.RunAsync(kit.Group, Ct);
        var second = gauges.Read();
        var state = await kit.StateAsync();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [], [StateFields.AllAck]);
        await SetOwnersAsync(kit, "k4");
        (await kit.Inventory.RunAsync(kit.Group, Ct))!.Canary.Should().BeNull();
        var unknown = gauges.Read();

        first.Should().BeEquivalentTo(new Dictionary<string, long> { ["cache.keymove.inventory_written_after_s3"] = 1, ["cache.keymove.inventory_expected_unmapped"] = 1 });
        second.Should().BeEquivalentTo(new Dictionary<string, long> { ["cache.keymove.inventory_written_after_s3"] = 2, ["cache.keymove.inventory_expected_unmapped"] = 2 });
        unknown.Should().BeEquivalentTo(new Dictionary<string, long> { ["cache.keymove.inventory_expected_unmapped"] = 3 });
    }

    [Fact]
    public async Task The_canary_uses_the_time_of_each_TTL_batch()
    {
        const int batchSize = 256;
        await using var kit = await DataOpsKit.StandaloneAsync(fixture, useScripts: true);
        kit.Kit.Time.Advance(TimeSpan.FromDays(1));
        kit.Space.MaxTtl["a"] = TimeSpan.FromMinutes(10);
        await SetAckedAsync(kit);
        await Task.WhenAll(Enumerable.Range(0, batchSize + 1).Select(i => kit.SetAsync(MoveSide.Legacy, "a", $"k{i}")));
        var scanned = 0;
        kit.Space.OnOwnerOf = _ =>
        {
            if (++scanned == batchSize + 1)
            {
                kit.Kit.Time.Advance(TimeSpan.FromMinutes(2));
            }
        };
        var ttl = Substitute.For<IDatabase>();
        ttl.KeyTimeToLiveAsync(Arg.Any<RedisKey>(), CommandFlags.DemandMaster).Returns((TimeSpan?)TimeSpan.FromMinutes(7));
        ttl.KeyTypeAsync(Arg.Any<RedisKey>(), CommandFlags.DemandMaster).Returns(RedisType.String);
        var inventory = new KeyInventory(kit.Scanner, kit.Space, kit.Kit.Store, ttl, kit.Prefix, kit.Kit.Time);

        var result = (await inventory.RunAsync(kit.Group, Ct))!;

        result.Residue["a"].Should().Be(batchSize + 1);
        result.Canary.Should().Be(1);
    }

    private static async Task SetOwnersAsync(DataOpsKit kit, string name, params string[] owners)
    {
        var key = await kit.SetAsync(MoveSide.Legacy, "c", name);
        kit.Space.OwnersByKey[key] = owners;
    }

    private static async Task SetAckedAsync(DataOpsKit kit)
    {
        var state = await kit.StateAsync();
        var t3 = kit.Kit.Time.GetUtcNow().AddMinutes(-2).ToUnixTimeMilliseconds();
        await kit.Kit.Scripts.FieldsAsync(kit.Kit.StateKey, state.Id, [(StateFields.T3At, t3.ToString(CultureInfo.InvariantCulture)), (StateFields.AllAck, state.Id)], []);
    }

    private sealed class GaugeReader : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _values = [];

        public GaugeReader(string group)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == KeyMoveTelemetry.MeterName && instrument.Name.StartsWith("cache.keymove.inventory_", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (tags.ToArray().Any(t => t.Key == "group" && (string?)t.Value == group))
                {
                    lock (_values)
                    {
                        _values[instrument.Name] = value;
                    }
                }
            });
            _listener.Start();
        }

        public Dictionary<string, long> Read()
        {
            lock (_values)
            {
                _values.Clear();
            }

            _listener.RecordObservableInstruments();
            lock (_values)
            {
                return new Dictionary<string, long>(_values);
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
