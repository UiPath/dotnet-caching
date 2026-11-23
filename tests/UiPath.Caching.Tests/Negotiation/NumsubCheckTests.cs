using System.Net;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Tests.Fakes;

namespace UiPath.Caching.Tests.Negotiation;

public class NumsubCheckTests
{
    private const string Why = "does not count clients";

    private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();
    private readonly ISubscriber _subscriber = Substitute.For<ISubscriber>();
    private readonly IRedisConnector _connector = Substitute.For<IRedisConnector>();
    private readonly FakeNodeListServer _nodeList = new();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    public NumsubCheckTests()
    {
        _nodeList.Database.Multiplexer.Returns(_multiplexer);
        _connector.Database.Returns(_nodeList.Database);
        _multiplexer.GetSubscriber(Arg.Any<object>()).Returns(_subscriber);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_subscriber_that_counts_as_one_is_reliable_and_the_probe_is_unsubscribed()
    {
        Wire(Server("a", _ => 1), Server("b", _ => 0));
        var sut = Source();

        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);

        await _subscriber.Received(1).SubscribeAsync(Arg.Is<RedisChannel>(c => c.ToString().StartsWith("caching-probe-", StringComparison.Ordinal)), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        await _subscriber.Received(1).UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task The_same_value_on_every_server_adds_up_to_more_than_one_and_latches()
    {
        Wire(Server("a", _ => 2), Server("b", _ => 2));
        var sut = Source();

        // The first reading may be a subscription the client restored twice after a reconnect, so only a second, on a fresh channel, latches.
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Inconclusive);
        _logger.DidNotReceiveWithAnyArgs().Log(LogLevel.Warning, default, default(object), default, default!);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.NotClientCounts);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.NotClientCounts);

        await _subscriber.Received(2).SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        _logger.ReceivedWithAnyArgs(1).Log(LogLevel.Warning, default, default(object), default, default!);
    }

    [Fact]
    public async Task A_count_of_more_than_one_that_the_next_check_does_not_repeat_is_reliable()
    {
        // StackExchange.Redis can restore one subscription on two cluster nodes after a reconnect, and a healthy cluster then counts two once.
        var calls = 0;
        Wire(Server("a", _ => Interlocked.Increment(ref calls) == 1 ? 2 : 1));
        var sut = Source();

        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Inconclusive);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);
        _logger.DidNotReceiveWithAnyArgs().Log(LogLevel.Warning, default, default(object), default, default!);
    }

    [Fact]
    public async Task A_count_of_more_than_one_then_an_inconclusive_check_then_more_than_one_again_latches()
    {
        var calls = 0;
        Wire(Server("a", _ => Interlocked.Increment(ref calls) switch { 1 => 2, 2 => 0, _ => 2 }));
        var sut = Source(wait: TimeSpan.Zero);

        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Inconclusive);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Inconclusive);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.NotClientCounts);
    }

    [Fact]
    public async Task A_subscription_that_has_not_shown_yet_is_inconclusive_and_is_asked_again()
    {
        var calls = 0;
        Wire(Server("a", _ => Interlocked.Increment(ref calls) < 3 ? 0 : 1));
        var sut = Source(wait: TimeSpan.Zero);

        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Inconclusive);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Inconclusive);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);
        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);

        await _subscriber.Received(3).SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
        await _subscriber.Received(3).UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_count_that_shows_within_the_wait_is_reliable()
    {
        var calls = 0;
        Wire(Server("a", _ => Interlocked.Increment(ref calls) < 3 ? 0 : 1));

        (await Source(wait: TimeSpan.FromSeconds(5)).CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);
    }

    [Fact]
    public async Task The_probe_is_unsubscribed_when_a_count_throws()
    {
        Wire(Server("a", _ => throw new InvalidOperationException("slow")));
        var sut = Source();

        var check = () => sut.CheckCountsAsync(Ct);

        await check.Should().ThrowAsync<InvalidOperationException>();
        await _subscriber.Received(1).UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task A_refusal_is_left_to_the_count_and_the_check_does_not_latch_it_as_unreliable()
    {
        Wire(Server("a", _ => throw new RedisCommandException("PUBSUB is disabled")));
        var sut = Source();

        (await sut.CheckCountsAsync(Ct)).Should().Be(NumsubCheck.Reliable);
        (await sut.CountAsync(new DnsEndPoint("a", 6379), ["x"], Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_list_that_reads_unknown_when_the_server_does_not_count_clients_and_stays_so()
    {
        var source = new FixedCheck(NumsubCheck.NotClientCounts);
        var list = new PubSubNodeList(source);

        var first = await list.ReadAsync(["c"], new Dictionary<string, long>(), Ct);
        var second = await list.ReadAsync(["c"], new Dictionary<string, long>(), Ct);

        first.Known.Should().BeFalse();
        first.Why.Should().Contain(Why);
        second.Should().BeEquivalentTo(first);
        source.Counted.Should().Be(0, "no count is trusted");
    }

    [Fact]
    public async Task A_list_reads_unknown_while_the_check_is_inconclusive_then_counts()
    {
        var source = new FixedCheck(NumsubCheck.Inconclusive);
        var list = new PubSubNodeList(source);

        var first = await list.ReadAsync(["c"], new Dictionary<string, long>(), Ct);
        source.Check = NumsubCheck.Reliable;
        var second = await list.ReadAsync(["c"], new Dictionary<string, long>(), Ct);

        first.Known.Should().BeFalse();
        first.Why.Should().Contain("could not be checked");
        second.Known.Should().BeTrue();
        source.Counted.Should().Be(1);
    }

    [Fact]
    public async Task A_list_over_a_source_that_does_not_check_counts_as_before()
    {
        var source = new FixedCheck(NumsubCheck.Reliable);

        (await new PubSubNodeList(source).ReadAsync(["c"], new Dictionary<string, long> { ["c"] = 0 }, Ct)).Known.Should().BeTrue();
    }

    private static IServer Server(string host, Func<int, long> count)
    {
        var server = Substitute.For<IServer>();
        server.EndPoint.Returns(new DnsEndPoint(host, 6379));
        server.IsConnected.Returns(true);
        server.ServerType.Returns(ServerType.Standalone);
        server.ExecuteAsync("PUBSUB", Arg.Any<ICollection<object>>(), Arg.Any<CommandFlags>()).Returns(call =>
        {
            var arguments = call.ArgAt<ICollection<object>>(1);
            if (arguments.First().Equals("NUMPAT"))
            {
                return Task.FromResult(RedisResult.Create((RedisValue)0));
            }

            long value;
            try
            {
                value = count(0);
            }
            catch (Exception ex)
            {
                return Task.FromException<RedisResult>(ex);
            }

            return Task.FromResult(RedisResult.Create([.. arguments.Skip(1).SelectMany(name => new[] { RedisResult.Create((RedisValue)(string)name, ResultType.BulkString), RedisResult.Create((RedisValue)value) })]));
        });
        return server;
    }

    private RedisSubscriberSource Source(TimeSpan? wait = null) => new(_connector, _logger) { CheckWait = wait ?? TimeSpan.FromSeconds(1) };

    private void Wire(params IServer[] servers)
    {
        _multiplexer.GetServers().Returns(servers);
        _multiplexer.GetServer(Arg.Any<EndPoint>()).Returns(call => servers.First(s => s.EndPoint.Equals(call.Arg<EndPoint>())));
    }

    private sealed class FixedCheck(NumsubCheck check) : ISubscriberSource
    {
        public NumsubCheck Check { get; set; } = check;

        public int Counted { get; private set; }

        public IReadOnlyList<EndPoint> Endpoints() => [new DnsEndPoint("a", 6379)];

        public Task<ServerCount?> CountAsync(EndPoint endpoint, IReadOnlyList<string> channels, CancellationToken cancellationToken)
        {
            Counted++;
            return Task.FromResult<ServerCount?>(new([.. channels.Select(_ => 0L)], 0));
        }

        public Task<NumsubCheck> CheckCountsAsync(CancellationToken cancellationToken) => Task.FromResult(Check);
    }
}
