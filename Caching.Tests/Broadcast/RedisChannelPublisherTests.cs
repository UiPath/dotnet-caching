using CloudNative.CloudEvents;
using CloudNative.CloudEvents.SystemTextJson;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace UiPath.Platform.Caching.Tests.Broadcast;

public class RedisChannelPublisherTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubsitute();
    private readonly ISerializerProxy _serializerProxy = new SystemJsonSerializerProxy();
    private IDatabase _database = default!;
    private string _channel = default!;
    private string _value = default!;

    [Fact]
    public async Task Canceling_token_stops_execution()
    {
        var sut = _fixture.Create<RedisChannelPublisher>();
        Channel channel = _fixture.Create<string>();
        var cloudEvent = new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            Type = "ClearCache",
            Source = new Uri("urn:machine"),
            DataContentType = "application/json",
            Data = _serializerProxy.Serialize(new ClearCacheEventData(Guid.NewGuid().ToString()))
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
        var sut = _fixture.Create<RedisChannelPublisher>();
        Channel channel = _fixture.Create<string>();
        var cloudEvent = new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            Type = "ClearCache",
            Source = new Uri("urn:machine"),
            DataContentType = "application/json",
            Data = _serializerProxy.Serialize(new ClearCacheEventData(Guid.NewGuid().ToString()))
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
        var sut = _fixture.Create<RedisChannelPublisher>();
        Channel channel = _fixture.Create<string>();
        var cloudEvent = new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            Type = "ClearCache",
            Source = new Uri("urn:machine"),
            DataContentType = "application/json",
            Data = new ClearCacheEventData(Guid.NewGuid().ToString())
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
        _fixture.Inject<CloudEventFormatter>(new JsonEventFormatter<ClearCacheEventData>());
        _fixture.Inject<ILogger<RedisChannelPublisher>>(NullLogger<RedisChannelPublisher>.Instance);
        _fixture.Inject(_serializerProxy);
        return Task.CompletedTask;
    }
}
