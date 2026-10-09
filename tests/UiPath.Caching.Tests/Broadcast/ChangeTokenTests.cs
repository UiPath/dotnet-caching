using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Broadcast;

public class ChangeTokenTests : IAsyncLifetime
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    private readonly RecordingTelemetryProvider _telemetryProvider = new();

    private string _key = default!;
    private TopicKey _topicKey = default!;
    private ITopic<ICacheEvent> _topic = default!;
    private CacheClearEventFormatterProxy _formatter = default!;
    private Uri? _source = null;
    private ISet<string>? _acceptedEvents = null;
    private SystemJsonByteSerializerProxy _serializer = default!;
    private ChangeToken<byte[]>? _sut = null;
    private ChangeToken<byte[]> Sut => _sut ??= new ChangeToken<byte[]>(_key, _topic, _source, _serializer, _fixture.Freeze<ILogger<ChangeToken<byte[]>>>(), _telemetryProvider, _acceptedEvents);

    public static IEnumerable<object[]> InvalidEvents() => new TestCacheEvent[]
    {
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Data = new CacheEventData(Guid.NewGuid().ToString()),
        },
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(Guid.NewGuid().ToString()),
        },
        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = null,
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
            Data = new CacheEventData(Guid.NewGuid().ToString()),
        },

        new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = null,
        },
    }.Select(cv => new object[] { cv });

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
            Data = new CacheEventData(_key),
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
    public void OnEventsMissed_expires_the_entry()
    {
        var callbackCalled = false;
        Sut.RegisterChangeCallback(_ => callbackCalled = true, null);

        ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);

        Sut.HasChanged.Should().BeTrue();
        callbackCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, MissedEventsReason.SubscriptionGap, true)]
    [InlineData(true, MissedEventsReason.SubscriptionGap, false)]
    [InlineData(true, MissedEventsReason.Lost, true)]
    public void A_token_that_ignores_subscription_gaps_still_expires_on_a_known_loss(bool ignoreSubscriptionGaps, MissedEventsReason reason, bool changed)
    {
        var sut = new ChangeToken<byte[]>(_key, _topic, _source, _serializer, _fixture.Freeze<ILogger<ChangeToken<byte[]>>>(), _telemetryProvider, _acceptedEvents, KeyMasker.Off, entryType: null, callerKey: _key, ignoreSubscriptionGaps);

        ((IMissedEventsObserver)sut).OnEventsMissed(reason);

        sut.HasChanged.Should().Be(changed);
    }

    [Fact]
    public void A_callback_registered_after_the_token_changed_runs_at_once()
    {
        ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);
        var called = false;

        Sut.RegisterChangeCallback(_ => called = true, null);

        called.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_notifications_and_registrations_do_not_collide()
    {
        var calls = 0;
        using var start = new Barrier(3);
        void Register()
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 20_000; i++)
            {
                Sut.RegisterChangeCallback(_ => Interlocked.Increment(ref calls), null);
            }
        }

        void Notify()
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            for (var i = 0; i < 200; i++)
            {
                ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);
            }
        }

        var register = Task.Run(Register, TestContext.Current.CancellationToken);
        var notify = Task.Run(Notify, TestContext.Current.CancellationToken);

        start.SignalAndWait(TestContext.Current.CancellationToken);

        await FluentActions.Awaiting(() => Task.WhenAll(register, notify)).Should().NotThrowAsync();
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
            Type = _fixture.Create<string>(),
        });
        Sut.HasChanged.Should().BeFalse();

        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key),
            Type = _acceptedEvents.First(),
        });
        Sut.HasChanged.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_removal_or_a_loss_after_a_refresh_leaves_nothing_to_refresh_with(bool loss)
    {
        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                ["_expiration_"] = DateTimeOffset.Now,
                ["_metadata_"] = new Dictionary<string, string?> { ["key"] = _key },
            }),
            Type = _fixture.Create<string>(),
        });

        if (loss)
        {
            ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);
        }
        else
        {
            Sut.OnNext(new TestCacheEvent { Id = Guid.NewGuid().ToString(), Source = new Uri("urn:machine"), Data = new CacheEventData(_key), Type = _fixture.Create<string>() });
        }

        Sut.HasChanged.Should().BeTrue();
        Sut.MetadataHasChanged.Should().BeFalse("the refresh callback would otherwise put the old value back");
        Sut.Expiration.Should().BeNull();
        Sut.Metadata.Should().BeNull();
    }

    [Theory]
    [InlineData("loss")]
    [InlineData("gap")]
    [InlineData("removal")]
    [InlineData("error")]
    public void A_refresh_delivered_after_a_removal_or_a_loss_leaves_nothing_to_refresh_with(string expiry)
    {
        var refreshable = new List<bool>();
        Sut.RegisterChangeCallback(_ => refreshable.Add(Sut.MetadataHasChanged || Sut.Expiration is not null), null);
        switch (expiry)
        {
            case "loss":
                ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);
                break;
            case "gap":
                ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.SubscriptionGap);
                break;
            case "removal":
                Sut.OnNext(new TestCacheEvent { Id = Guid.NewGuid().ToString(), Source = new Uri("urn:machine"), Data = new CacheEventData(_key), Type = _fixture.Create<string>() });
                break;
            default:
                Sut.OnError(_fixture.Create<Exception>());
                break;
        }

        // The dispatcher already held this observer when the loss was reported.
        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                ["_expiration_"] = DateTimeOffset.Now.AddMinutes(5),
                ["_metadata_"] = new Dictionary<string, string?> { ["key"] = _key },
            }),
            Type = _fixture.Create<string>(),
        });

        refreshable.Should().Equal([false, false], "the refresh callback would otherwise put the old value back");
        Sut.Expiration.Should().BeNull();
        Sut.Metadata.Should().BeNull();
    }

    [Fact]
    public void Two_refreshes_both_carry_their_expiration()
    {
        foreach (var minutes in new[] { 5, 10 })
        {
            Sut.OnNext(new TestCacheEvent
            {
                Id = Guid.NewGuid().ToString(),
                Source = new Uri("urn:machine"),
                Data = new CacheEventData(_key, new Dictionary<string, object?> { ["_expiration_"] = DateTimeOffset.UnixEpoch.AddMinutes(minutes) }),
                Type = _fixture.Create<string>(),
            });
        }

        Sut.Expiration.Should().Be(DateTimeOffset.UnixEpoch.AddMinutes(10), "only an expiry ends refreshing");
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
                },
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
                },
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
                },
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
        _fixture.Freeze<ILogger<ChangeToken<byte[]>>>();
        _topic = _fixture.Freeze<ITopic<ICacheEvent>>();
        _formatter = new CacheClearEventFormatterProxy();
        _serializer = new SystemJsonByteSerializerProxy();
        _fixture.Inject<IEventFormatterProxy<ICacheEvent>>(_formatter);
        return ValueTask.CompletedTask;
    }
}
