using System.Net;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

public class RedisSubscriberSourceTests
{
    private const string Channel = "app:ps:orders";

    private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();
    private readonly IRedisConnector _connector = Substitute.For<IRedisConnector>();
    private readonly FakeNodeListServer _nodeList = new();
    private readonly List<string[]> _numsub = [];

    public RedisSubscriberSourceTests()
    {
        _nodeList.Database.Multiplexer.Returns(_multiplexer);
        _connector.Database.Returns(_nodeList.Database);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_server_that_is_not_connected_makes_the_whole_list_fail()
    {
        Wire(Server("a", connected: true), Server("b", connected: false));

        var endpoints = () => new RedisSubscriberSource(_connector).Endpoints();

        endpoints.Should().Throw<InvalidOperationException>().WithMessage("*b*not connected*");
    }

    [Fact]
    public void Every_connected_server_is_listed_and_a_sentinel_is_not_one()
    {
        Wire(Server("a", true), Server("b", true), Server("s", true, ServerType.Sentinel));

        var endpoints = new RedisSubscriberSource(_connector).Endpoints();

        endpoints.Select(e => ((DnsEndPoint)e).Host).Should().Equal("a", "b");
    }

    [Fact]
    public async Task With_one_server_down_the_view_fails_and_binary_events_are_not_enabled()
    {
        Wire(Server("a", true, count: 1), Server("b", connected: false));
        var negotiator = new FormatNegotiator([], new FormatNegotiationOptions { UpgradeHold = TimeSpan.Zero }, TimeProvider.System, events: new DesiredEvents(["02"]));
        var sut = new FormatNegotiationService(new FormatNegotiation
        {
            Negotiator = negotiator,
            Advertisement = new("3.2.0", "me", ["json"], new Dictionary<string, IReadOnlyList<string>>(), EventReads: ["json", "02"]),
            NodeId = "me",
            Store = new NodeListStore(_nodeList.Connector, "ads"),
            NodeList = new StreamNodeList(new NoGroups(), new FormatNegotiationOptions()),
            PubSub = new Subscriptions(),
            Subscribers = new PubSubNodeList(new RedisSubscriberSource(_connector)),
            Options = new FormatNegotiationOptions { UpgradeHold = TimeSpan.Zero },
        });
        await sut.AdvertiseAsync(Ct);

        for (var view = 0; view < 4; view++)
        {
            await sut.RefreshAsync(Ct);
        }

        negotiator.Writes.Should().BeFalse("the sum over the servers it can reach hides the subscribers of the one it cannot");

        // The same servers, all connected: the count is whole, and the node that holds the only subscription may write.
        Wire(Server("a", true, count: 1), Server("b", true));
        await sut.RefreshAsync(Ct);
        await sut.RefreshAsync(Ct);
        negotiator.Writes.Should().BeTrue();
    }

    [Fact]
    public async Task The_channel_names_it_asks_for_carry_the_connections_channel_prefix()
    {
        var server = Server("a", true, count: 1);
        Wire(server);
        _multiplexer.Configuration.Returns("a:6379,channelPrefix=pfx:");
        var source = new RedisSubscriberSource(_connector);

        await source.CountAsync(source.Endpoints()[0], [Channel, "other"], Ct);

        _numsub.Should().ContainSingle().Which.Should().Equal($"pfx:{Channel}", "pfx:other");
    }

    [Fact]
    public async Task Without_a_prefix_the_names_are_asked_as_they_are()
    {
        var server = Server("a", true, count: 1);
        Wire(server);
        _multiplexer.Configuration.Returns("a:6379");
        var source = new RedisSubscriberSource(_connector);

        await source.CountAsync(source.Endpoints()[0], [Channel], Ct);

        _numsub.Should().ContainSingle().Which.Should().Equal(Channel);
    }

    private void Wire(params IServer[] servers)
    {
        _multiplexer.GetServers().Returns(servers);
        _multiplexer.GetServer(Arg.Any<EndPoint>()).Returns(call => servers.First(s => s.EndPoint.Equals(call.Arg<EndPoint>())));
    }

    private IServer Server(string host, bool connected, ServerType type = ServerType.Standalone, long count = 0)
    {
        var server = Substitute.For<IServer>();
        server.EndPoint.Returns(new DnsEndPoint(host, 6379));
        server.IsConnected.Returns(connected);
        server.ServerType.Returns(type);
        server.ExecuteAsync("PUBSUB", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(call =>
        {
            var arguments = call.ArgAt<ICollection<object>>(1);
            var first = arguments.First();
            if (first.Equals("NUMSUB"))
            {
                _numsub.Add([.. arguments.Skip(1).Select(argument => (string)argument)]);
            }

            return Task.FromResult(first.Equals("NUMPAT")
                ? RedisResult.Create((RedisValue)0)
                : RedisResult.Create([.. arguments.Skip(1).SelectMany(name => new[] { RedisResult.Create((RedisValue)(string)name, ResultType.BulkString), RedisResult.Create((RedisValue)count) })]));
        });
        return server;
    }

    private sealed class NoGroups : IStreamGroupSource
    {
        public Task<IReadOnlyList<string>> GroupsAsync(RedisKey stream, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<TimeSpan?> IdleAsync(RedisKey stream, string group, CancellationToken cancellationToken) => Task.FromResult<TimeSpan?>(null);
    }

    private sealed class Subscriptions : IPubSubSubscriptions
    {
        public IReadOnlyList<string> Channels() => [Channel];

        public IReadOnlyList<string> Uncountable() => [];

        public IReadOnlyDictionary<string, int> Confirmed() => new Dictionary<string, int> { [Channel] = 1 };

        public int Patterns() => 0;

        public void WhenConfirmed(Action handler)
        {
        }
    }
}
