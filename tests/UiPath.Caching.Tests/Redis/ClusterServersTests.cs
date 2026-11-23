using System.Net;
using StackExchange.Redis;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Redis;

public class ClusterServersTests
{
    private static readonly IPEndPoint ShardA = new(IPAddress.Parse("20.8.21.125"), 8502);
    private static readonly IPEndPoint ShardB = new(IPAddress.Parse("20.8.21.125"), 8503);
    private static readonly DnsEndPoint Entry = new("host.redis.azure.net", 10000);

    [Fact]
    public void The_entry_endpoint_is_dropped_and_the_nodes_stay()
    {
        var entry = Server(Entry);
        var a = Server(ShardA);
        var b = Server(ShardB);
        var reader = new Reader(new() { [entry] = [ShardA, ShardB], [a] = [ShardA, ShardB], [b] = [ShardA, ShardB] });

        ClusterServers.Nodes([entry, a, b], reader).Should().Equal(a, b);
    }

    [Fact]
    public void A_standalone_server_passes_through()
    {
        var one = Server(new DnsEndPoint("localhost", 6379), ServerType.Standalone);
        var two = Server(new DnsEndPoint("other", 6379), ServerType.Standalone);

        ClusterServers.Nodes([one, two], new Reader([])).Should().Equal(one, two);
    }

    [Fact]
    public void Cluster_servers_without_a_configuration_are_all_kept()
    {
        var entry = Server(Entry);
        var a = Server(ShardA);

        ClusterServers.Nodes([entry, a], new Reader(new() { [entry] = null, [a] = null })).Should().Equal(entry, a);
    }

    [Fact]
    public void One_configuration_is_enough_to_judge_the_servers_that_have_none()
    {
        var entry = Server(Entry);
        var a = Server(ShardA);
        var b = Server(ShardB);

        ClusterServers.Nodes([entry, a, b], new Reader(new() { [a] = [ShardA, ShardB] })).Should().Equal(a, b);
    }

    [Fact]
    public void A_configuration_without_nodes_leaves_the_servers_as_they_are()
    {
        var entry = Server(Entry);
        var a = Server(ShardA);

        ClusterServers.Nodes([entry, a], new Reader(new() { [entry] = [], [a] = [] })).Should().Equal(entry, a);
    }

    [Fact]
    public void A_filter_that_would_leave_nothing_returns_the_input()
    {
        var entry = Server(Entry);
        var other = Server(new DnsEndPoint("other.example", 7000));

        ClusterServers.Nodes([entry, other], new Reader(new() { [entry] = [ShardA, ShardB] })).Should().Equal(entry, other);
    }

    [Fact]
    public void A_server_that_is_not_connected_does_not_vote_its_configuration()
    {
        var entry = Server(Entry);
        var a = Server(ShardA, connected: false);

        ClusterServers.Nodes([entry, a], new Reader(new() { [a] = [ShardA] })).Should().Equal(entry, a);
    }

    [Fact]
    public void A_non_cluster_server_beside_a_cluster_is_kept()
    {
        var entry = Server(Entry);
        var a = Server(ShardA);
        var plain = Server(new DnsEndPoint("plain", 6379), ServerType.Standalone);

        ClusterServers.Nodes([entry, a, plain], new Reader(new() { [a] = [ShardA] })).Should().Equal(a, plain);
    }

    [Fact]
    public void Replicas_stay_in_the_list_for_the_callers_to_filter()
    {
        var a = Server(ShardA);
        var replica = Server(ShardB, replica: true);

        var nodes = ClusterServers.Nodes([a, replica], new Reader(new() { [a] = [ShardA, ShardB] }));

        nodes.Should().Equal(a, replica);
        nodes.Where(static s => !s.IsReplica && s.IsConnected).Should().Equal(a);
    }

    [Fact]
    public void A_substituted_server_with_no_cluster_configuration_passes_with_the_real_reader()
    {
        var a = Server(ShardA);

        ClusterServers.Nodes([a]).Should().Equal(a);
    }

    private static IServer Server(EndPoint endpoint, ServerType type = ServerType.Cluster, bool connected = true, bool replica = false)
    {
        var server = Substitute.For<IServer>();
        server.EndPoint.Returns(endpoint);
        server.ServerType.Returns(type);
        server.IsConnected.Returns(connected);
        server.IsReplica.Returns(replica);
        return server;
    }

    /// <summary>ClusterConfiguration has no public constructor, so a server "has one" when the reader hands back a token for it.</summary>
    private sealed class Reader(Dictionary<IServer, EndPoint[]?> members) : IClusterTopologyReader
    {
        public object? GetConfiguration(IServer server) => members.TryGetValue(server, out var found) && found is not null ? found : null;

        public HashSet<EndPoint> GetMembers(object configuration) => [.. (EndPoint[])configuration];
    }
}
