using System.Net;
using System.Reflection;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The replicas a scan unit lists, offline: standalone and cluster, with one that is down, one flagged failed and the replicas of another primary.
public sealed class ScanUnitListingTests
{
    [Fact]
    public void A_standalone_unit_lists_every_replica_and_a_disconnected_one_has_no_server()
    {
        var primary = FakeServer.Make(Endpoint(1), connected: true, replica: false);
        var up = FakeServer.Make(Endpoint(2), connected: true, replica: true);
        var down = FakeServer.Make(Endpoint(3), connected: false, replica: true);

        var unit = MultiplexerScanUnits.Standalone([primary, up, down]).Single();

        unit.Id.Should().Be("10.0.0.1:6379");
        unit.Primary.Should().BeSameAs(primary);
        unit.Replica.Should().BeSameAs(up, "the scan still reads a connected replica");
        unit.Replicas.Select(r => (r.Name, Up: r.Server is not null)).Should().Equal(("10.0.0.2:6379", true), ("10.0.0.3:6379", false));
        unit.ReplicasToJudge.Should().HaveCount(2);
    }

    [Fact]
    public void A_cluster_unit_lists_the_replicas_of_its_own_primary_and_marks_the_failed_and_the_unreachable_as_down()
    {
        var m1 = FakeServer.Make(Endpoint(1), true, false);
        var m2 = FakeServer.Make(Endpoint(2), true, false);
        var r1 = FakeServer.Make(Endpoint(11), true, true);
        var r3 = FakeServer.Make(Endpoint(13), true, true);
        var other = FakeServer.Make(Endpoint(21), true, true);
        var nodes = new[]
        {
            new TopologyNode("m1", Endpoint(1), false, false, null, true),
            new TopologyNode("m2", Endpoint(2), false, false, null, true),
            new TopologyNode("noslots", Endpoint(9), false, false, null, false),
            new TopologyNode("r1", Endpoint(11), true, false, "m1", false),
            new TopologyNode("r2", Endpoint(12), true, false, "m1", false),
            new TopologyNode("r3", Endpoint(13), true, true, "m1", false),
            new TopologyNode("r4", Endpoint(21), true, false, "m2", false),
        };

        var units = MultiplexerScanUnits.Cluster([m1, m2, r1, r3, other], nodes);

        units.Select(u => u.Id).Should().Equal("m1", "m2");
        var first = units[0];
        first.Replica.Should().BeSameAs(r1);
        first.Replicas.Select(r => (r.Name, Up: r.Server is not null)).Should().Equal(("r1", true), ("r2", false), ("r3", false));
        units[1].Replicas.Select(r => r.Name).Should().Equal("r4");
    }

    [Fact]
    public void A_cluster_primary_that_is_not_connected_cannot_be_scanned()
    {
        var act = () => MultiplexerScanUnits.Cluster([], [new TopologyNode("m1", Endpoint(1), false, false, null, true)]);

        act.Should().Throw<RedisException>().WithMessage("*not connected*");
    }

    private static IPEndPoint Endpoint(int last) => new(IPAddress.Parse("10.0.0." + last), 6379);

    /// <summary>An <see cref="IServer"/> that answers only what the unit listing asks.</summary>
    public class FakeServer : DispatchProxy
    {
        private EndPoint _endpoint = default!;
        private bool _connected;
        private bool _replica;

        public static IServer Make(EndPoint endpoint, bool connected, bool replica)
        {
            var proxy = Create<IServer, FakeServer>();
            var fake = (FakeServer)(object)proxy;
            (fake._endpoint, fake._connected, fake._replica) = (endpoint, connected, replica);
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Name switch
        {
            "get_EndPoint" => _endpoint,
            "get_IsConnected" => _connected,
            "get_IsReplica" => _replica,
            "get_ServerType" => ServerType.Standalone,
            _ => throw new NotSupportedException(targetMethod.Name),
        };
    }
}
