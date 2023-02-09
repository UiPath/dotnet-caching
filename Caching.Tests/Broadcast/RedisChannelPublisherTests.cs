using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisChannelPublisherTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private IDatabase _database = default!;
    private string _channel = default!;
    private string _value = default!;
    private PubSubEventFormatterProxy _formatter = default!;

    [Fact]
    public async Task Canceling_token_stops_execution()
    {
        var sut = _fixture.Create<RedisChannelPublisher<IPubSubEvent>>();
        Channel channel = _fixture.Create<string>();
        var cloudEvent = new TestPubSubEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        var cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;
        cancelSource.Cancel();
        Func<Task> act = async () => { await sut.PublishAsync(channel, cloudEvent, token); };
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task No_exceptions_are_thrown_when_redis_fails()
    {
        var sut = _fixture.Create<RedisChannelPublisher<IPubSubEvent>>();
        Channel channel = _fixture.Create<string>();
        var cloudEvent = new TestPubSubEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        _database.ClearReceivedCalls();
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisException("test"));
        Func<Task> act = async () => { await sut.PublishAsync(channel, cloudEvent); };
        await act.Should().NotThrowAsync();
        _channel.Should().BeNull();
        _value.Should().BeNull();
    }

    [Fact]
    public async Task Publish_works_as_expected()
    {
        var sut = _fixture.Create<RedisChannelPublisher<IPubSubEvent>>();
        Channel channel = _fixture.Create<string>();
        var cloudEvent = new TestPubSubEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine")
        };
        _database.ClearReceivedCalls();
        var executed = false;
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(c =>
            {
                executed = true;
                return _fixture.Create<long>();
            });
        await sut.PublishAsync(channel, cloudEvent);
        executed.Should().BeTrue();
    }


    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        _database = _fixture.Freeze<IDatabase>();
        _fixture.Inject<Func<IDatabase>>(() => _database);
        _database.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .ReturnsForAnyArgs(c =>
            {
                _channel = c.Arg<RedisChannel>()!;
                return 1;
            });
        _formatter = new PubSubEventFormatterProxy();
        _fixture.Inject<IEventFormatterProxy<IPubSubEvent>>(_formatter);
        _fixture.Inject<ILogger<RedisChannelPublisher<IPubSubEvent>>>(NullLogger<RedisChannelPublisher<IPubSubEvent>>.Instance);
        return Task.CompletedTask;
    }
}
