using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

public class ChangeTokenTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private string _key = default!;
    private TopicKey _topicKey = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private CacheClearEventFormatterProxy _formatter = default!;
    private Uri? _source = null;
    private ISet<string>? _acceptedEvents = null;
    private ISerializerProxy<RedisValue> _serializer = default!;

    private readonly RecordingTelemetryProvider _telemetryProvider = new();
    private ChangeToken<RedisValue>? _sut = null;
    private ChangeToken<RedisValue> Sut => _sut ??= new ChangeToken<RedisValue>(_key, _topic, _source, _serializer, _fixture.Freeze<ILogger<ChangeToken<RedisValue>>>(), _telemetryProvider, _acceptedEvents);

    [Fact]
    public void Verify_ActiveChangeCallbacks()
    {
        Sut.ActiveChangeCallbacks.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public void OnNext_NoChanges_wheniInvalid_event(TestCacheEvent cloudEvent)
    {
        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };
        var d = Sut.RegisterChangeCallback(callback, expectedState);

        Sut.OnNext(cloudEvent);
        Sut.HasChanged.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }

    [Theory]
    [InlineData("urn:machine", false)]
    [InlineData("urn:another-machine", true)]
    [InlineData(null, true)]
    public void OnNext_Changes_when_corect_key(string? source, bool hasChanged)
    {
        if (!string.IsNullOrWhiteSpace(source))
        {
            _source = new Uri(source);
        }

        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };

        var d = Sut.RegisterChangeCallback(callback, expectedState);
        var cloudEVent = new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key)
        };
        Sut.OnNext(cloudEVent);
        Sut.HasChanged.Should().Be(hasChanged);
        callbackCalled.Should().Be(hasChanged);
    }


    [Fact]
    public void OnComplete()
    {
        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };

        var d = Sut.RegisterChangeCallback(callback, expectedState);
        Sut.OnCompleted();
        Sut.HasChanged.Should().BeFalse();
        callbackCalled.Should().BeFalse();
    }

    [Fact]
    public void OnError()
    {
        object? actualState = null;
        var callbackCalled = false;
        var expectedState = _fixture.Create<object>();
        Action<object?> callback = (state) =>
        {
            callbackCalled = true;
            actualState = state;
        };

        var d = Sut.RegisterChangeCallback(callback, expectedState);
        Sut.OnError(_fixture.Create<Exception>());
        Sut.HasChanged.Should().BeTrue();
        callbackCalled.Should().BeTrue();
    }

    [Fact]
    public void AcceptedEvents()
    {
        _acceptedEvents = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() }.ToHashSet();
        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key),
            Type = _fixture.Create<string>()
        });
        Sut.HasChanged.Should().BeFalse();

        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key),
            Type = _acceptedEvents.First()
        });
        Sut.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Events_with_extended_data()
    {
        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                ["_expiration_"] = DateTimeOffset.Now,
                ["_metadata_"] = new Dictionary<string, string?>
                {
                    ["key"] = _key,
                }
            }),
            Type = _fixture.Create<string>(),
            
        });
        Sut.HasChanged.Should().BeTrue();
        Sut.MetadataHasChanged.Should().BeTrue();
        Sut.Metadata.Should().NotBeNull();
    }

    [Fact]
    public void Accepted_event_emits_telemetry_on_read()
    {
        var transportId = "123456789013-1";
        Sut.OnNext(new TestCacheEvent
        {
            TransportId = transportId,
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                ["_expiration_"] = DateTimeOffset.Now,
                ["_metadata_"] = new Dictionary<string, string?>
                {
                    ["key"] = _key,
                }
            }),
            Type = _fixture.Create<string>(),

        });
        _telemetryProvider.Metrics.Should().ContainSingle(m =>
            m.Name == Metrics.GetReadTopicMetricName(_topicKey) && m.Value == 123456789013);
        Sut.HasChanged.Should().BeTrue();
        Sut.MetadataHasChanged.Should().BeTrue();
        Sut.Metadata.Should().NotBeNull();
    }

    [Fact]
    public void Accepted_event_emits_telemetry_on_unaccepted_read()
    {
        var transportId = "123456789013-1";
        Sut.OnNext(new TestCacheEvent
        {
            TransportId = transportId,
            Id = Guid.NewGuid().ToString(),
            Source = _source,
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                ["_expiration_"] = DateTimeOffset.Now,
                ["_metadata_"] = new Dictionary<string, string?>
                {
                    ["key"] = _key,
                }
            }),
            Type = _fixture.Create<string>(),

        });
        _telemetryProvider.Metrics.Should().ContainSingle(m =>
            m.Name == Metrics.GetReadTopicMetricName(_topicKey) && m.Value == 123456789013);
    }


    [Fact]
    public void Dispose_token()
    {
        var disposable = _fixture.Create<IDisposable>();
        _topic.Subscribe(Arg.Any<IObserver<ICacheEvent>>())
            .Returns(disposable);
        Sut.Dispose();
        disposable.Received(1).Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync()
    {
        _key = _fixture.Freeze<string>();
        _topicKey = (TopicKey)_fixture.Create<string>();
        _fixture.Inject(_topicKey);
        _fixture.Freeze<ILogger<ChangeToken<RedisValue>>>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _formatter = new CacheClearEventFormatterProxy();
        _serializer = new SystemJsonSerializerProxy();
        _fixture.Inject<IEventFormatterProxy<ICacheEvent>>(_formatter);
        return ValueTask.CompletedTask;
    }

    public static IEnumerable<object[]> InvalidEvents() => new TestCacheEvent[]
    {
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = new CacheEventData(Guid.NewGuid().ToString())
        },
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(Guid.NewGuid().ToString())
        },
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = null
        },
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
        },
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(Guid.NewGuid().ToString())
        },

        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = null
        }
    }.Select(cv => new object[] { cv });
}
