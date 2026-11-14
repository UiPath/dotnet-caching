using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData("event", false)]
    [InlineData("loss", true)]
    [InlineData("error", true)]
    public void The_invalidating_callback_runs_before_the_token_changes(string cause, bool lost)
    {
        var seen = new List<(object? State, bool Lost, bool HasChanged)>();
        var state = new object();
        ((IInvalidationSource)Sut).RegisterInvalidatingCallback((s, l) => seen.Add((s, l, Sut.HasChanged)), state);

        switch (cause)
        {
            case "event":
                Sut.OnNext(new TestCacheEvent { Id = "1", Source = new Uri("urn:peer"), Data = new CacheEventData(_key), Type = _fixture.Create<string>() });
                break;
            case "loss":
                ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);
                break;
            default:
                Sut.OnError(_fixture.Create<Exception>());
                break;
        }

        Sut.HasChanged.Should().BeTrue();
        seen.Should().Equal((state, lost, false));
    }

    [Fact]
    public void An_invalidating_callback_registered_after_the_token_changed_runs_at_once()
    {
        ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);
        var seen = new List<bool>();

        ((IInvalidationSource)Sut).RegisterInvalidatingCallback((_, l) => seen.Add(l), null);

        seen.Should().Equal(true);
    }

    [Fact]
    public void The_invalidating_callback_runs_for_a_peer_update_the_holder_applied()
    {
        var seen = 0;
        ((IInvalidationSource)Sut).RegisterInvalidatingCallback((_, _) => seen++, null);
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = _ => true;

        Sut.OnNext(new TestCacheEvent
        {
            Id = "1",
            Source = new Uri("urn:peer"),
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                [KnownFieldNames.VersionKey] = 2L,
                [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, byte[]?> { ["a"] = [1] },
            }),
            Type = _fixture.Create<string>(),
        });

        Sut.HasChanged.Should().BeFalse();
        seen.Should().Be(1);
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
    public void A_peer_set_taken_by_the_refresh_hook_leaves_the_token_unchanged()
    {
        var state = new object();
        var seen = new List<object?>();
        var changed = false;
        Sut.RegisterChangeCallback(_ => changed = true, null);
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(s => { seen.Add(s); return true; }, state);

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        seen.Should().Equal(state);
        Sut.HasChanged.Should().BeFalse();
        changed.Should().BeFalse();
    }

    [Fact]
    public void A_peer_set_the_hook_declines_changes_the_token()
    {
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => false, null);

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        Sut.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void A_peer_set_the_hook_declines_clears_a_refreshed_expiration()
    {
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => false, null);
        Sut.OnNext(PeerEvent(KnownEventTypes.CacheRefreshed, new Dictionary<string, object?> { ["_expiration_"] = DateTimeOffset.Now.AddMinutes(5) }));
        Sut.Expiration.Should().NotBeNull();

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        Sut.Expiration.Should().BeNull();
    }

    [Fact]
    public void A_peer_set_whose_hook_throws_still_changes_the_token()
    {
        var changed = false;
        Sut.RegisterChangeCallback(_ => changed = true, null);
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => throw new InvalidOperationException("hook failed"), null);

        Sut.Invoking(s => s.OnNext(PeerEvent(KnownEventTypes.CacheSet))).Should().NotThrow();

        Sut.HasChanged.Should().BeTrue();
        changed.Should().BeTrue();
    }

    [Fact]
    public void A_pending_peer_set_whose_hook_throws_still_changes_the_token()
    {
        var calls = 0;
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => ++calls == 1 ? true : throw new InvalidOperationException("hook failed"), null);
        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        Sut.Invoking(s => s.OnNext(PeerEvent(KnownEventTypes.CacheSet))).Should().NotThrow();

        Sut.HasChanged.Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public void A_peer_removal_never_reaches_the_refresh_hook()
    {
        var calls = 0;
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => { calls++; return true; }, null);

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheRemoved));

        calls.Should().Be(0);
        Sut.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void A_refresh_event_after_a_pending_peer_set_drops_the_entry()
    {
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => true, null);
        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheRefreshed, new Dictionary<string, object?> { ["_expiration_"] = DateTimeOffset.Now }));

        Sut.HasChanged.Should().BeTrue();
        Sut.Expiration.Should().BeNull();
        Sut.MetadataHasChanged.Should().BeFalse();
    }

    [Fact]
    public void A_second_peer_set_while_pending_reaches_the_hook_again()
    {
        var calls = 0;
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => { calls++; return true; }, null);

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));
        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        calls.Should().Be(2);
        Sut.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void A_loss_while_a_refresh_is_pending_still_drops()
    {
        var calls = 0;
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => { calls++; return true; }, null);
        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        ((IMissedEventsObserver)Sut).OnEventsMissed(MissedEventsReason.Lost);

        Sut.HasChanged.Should().BeTrue();
        calls.Should().Be(1);
    }

    [Fact]
    public void The_invalidating_callback_runs_before_the_refresh_hook()
    {
        var order = new List<string>();
        ((IInvalidationSource)Sut).RegisterInvalidatingCallback((_, _) => order.Add("invalidating"), null);
        ((IPeerRefreshSource)Sut).RegisterPeerRefresh(_ => { order.Add("refresh"); return true; }, null);

        Sut.OnNext(PeerEvent(KnownEventTypes.CacheSet));

        order.Should().Equal("invalidating", "refresh");
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

    [Theory]
    [InlineData("no handler", 7L)]
    [InlineData("handler declines", 7L)]
    [InlineData("malformed version", 0L)]
    public void A_write_event_carrying_fields_that_is_not_applied_expires_the_entry_so_a_later_refresh_cannot_renew_it(string case_, long version)
    {
        var refreshable = new List<bool>();
        Sut.RegisterChangeCallback(_ => refreshable.Add(Sut.MetadataHasChanged || Sut.Expiration is not null), null);
        if (case_ == "handler declines")
        {
            ((IPeerUpdateSource)Sut).PeerUpdateHandler = _ => false;
        }

        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key, new Dictionary<string, object?>
            {
                [KnownFieldNames.VersionKey] = version,
                [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, byte[]?> { ["a"] = [1] },
            }),
            Type = _fixture.Create<string>(),
        });
        Sut.OnNext(new TestCacheEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = new Uri("urn:machine"),
            Data = new CacheEventData(_key, new Dictionary<string, object?> { ["_expiration_"] = DateTimeOffset.Now.AddMinutes(5) }),
            Type = _fixture.Create<string>(),
        });

        refreshable.Should().Equal([false, false], "the refresh callback would otherwise put the old value back");
        Sut.Expiration.Should().BeNull();
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

    [Fact]
    public void Typed_properties_are_used_without_the_value_serializer()
    {
        var strict = new ThrowingSerializer();
        var token = new ChangeToken<byte[]>(_key, _topic, _source, strict, _fixture.Freeze<ILogger<ChangeToken<byte[]>>>(), _telemetryProvider, _acceptedEvents);
        PeerUpdate? seen = null;
        ((IPeerUpdateSource)token).PeerUpdateHandler = update =>
        {
            seen = update;
            return true;
        };
        var deadline = DateTimeOffset.UnixEpoch.AddDays(9);

        token.OnNext(PeerEvent("set", new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 7L,
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, byte[]?> { ["a"] = [1, 2] },
            [KnownFieldNames.PeerExpirationKey] = deadline,
            [KnownFieldNames.PeerMetadataKey] = new Dictionary<string, string?> { ["m"] = "v" },
        }));
        token.OnNext(PeerEvent("refreshed", new Dictionary<string, object?>
        {
            [KnownFieldNames.ExpirationKey] = deadline,
            [KnownFieldNames.MetadataKey] = new Dictionary<string, string?> { ["m"] = "v" },
        }));

        seen.Should().NotBeNull();
        seen!.Version.Should().Be(7);
        seen.Fields["a"].Should().Equal(1, 2);
        seen.Expiration.Should().Be(deadline);
        seen.Metadata.Should().Equal(new Dictionary<string, string?> { ["m"] = "v" });
        token.Expiration.Should().Be(deadline);
        token.Metadata.Should().Equal(new Dictionary<string, string?> { ["m"] = "v" });
        strict.Calls.Should().Be(0);
    }

    [Fact]
    public void JsonElement_properties_still_use_the_fallback()
    {
        PeerUpdate? seen = null;
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = update =>
        {
            seen = update;
            return true;
        };
        var deadline = DateTimeOffset.UnixEpoch.AddDays(9);
        var json = """{"_version_":7,"_peerFields_":{"a":"AQI="},"_peerExpiration_":@,"_peerMetadata_":{"m":"v"},"_expiration_":@,"_metadata_":{"m":"v"}}""".Replace("@", JsonSerializer.Serialize(deadline));
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => (object?)p.Value);

        Sut.OnNext(PeerEvent("set", properties));

        seen.Should().NotBeNull();
        seen!.Version.Should().Be(7);
        seen.Fields["a"].Should().Equal(1, 2);
        seen.Expiration.Should().Be(deadline);
        seen.Metadata.Should().Equal(new Dictionary<string, string?> { ["m"] = "v" });
    }

    [Fact]
    public void JsonElement_peer_fields_are_requested_from_the_serializer_as_base64_strings()
    {
        var recording = new RecordingSerializer();
        var token = new ChangeToken<byte[]>(_key, _topic, _source, recording, _fixture.Freeze<ILogger<ChangeToken<byte[]>>>(), _telemetryProvider, _acceptedEvents);
        PeerUpdate? seen = null;
        ((IPeerUpdateSource)token).PeerUpdateHandler = update =>
        {
            seen = update;
            return true;
        };
        var json = """{"_version_":7,"_peerFields_":{"a":"AQI="}}""";
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => (object?)p.Value);

        token.OnNext(PeerEvent("set", properties));

        recording.Requested.Should().Contain(typeof(IDictionary<string, string?>));
        recording.Requested.Should().NotContain(typeof(IReadOnlyDictionary<string, byte[]?>));
        seen.Should().NotBeNull();
        seen!.Fields["a"].Should().Equal(1, 2);
    }

    [Fact]
    public void Null_JsonElement_metadata_still_applies_the_peer_update()
    {
        PeerUpdate? seen = null;
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = update =>
        {
            seen = update;
            return true;
        };
        var json = """{"_version_":7,"_peerFields_":{"a":"AQI="},"_peerMetadata_":null,"_metadata_":null}""";
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(p => p.Key, p => (object?)p.Value);

        Sut.Invoking(s => s.OnNext(PeerEvent("set", properties))).Should().NotThrow();

        seen.Should().NotBeNull();
        seen!.Metadata.Should().BeNull();
        Sut.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task A_published_event_that_never_left_the_process_applies_as_a_peer_update()
    {
        CacheEventData? published = null;
        var factory = Substitute.For<ICacheEventFactory>();
        factory.Create("cache", Arg.Any<string>(), Arg.Any<CacheEventData>(), Arg.Any<string?>())
            .Returns(call => new CacheEvent { Id = "1", Source = new Uri("urn:machine"), Type = call.ArgAt<string>(1), Data = published = call.Arg<CacheEventData>() });
        var topics = Substitute.For<ITopicProvider>();
        topics.Create(Arg.Any<TopicKey>()).PublishAsync(Arg.Any<ICacheEvent>(), Arg.Any<CancellationToken>()).Returns(true);
        var options = new CacheEntryOptions { CacheKey = new CacheKey(_key), TopicKey = "t", Metadata = new Dictionary<string, string?> { ["m"] = "v" } };
        await new CacheEventPublisher("cache", topics, factory, NullLogger.Instance)
            .CacheSetAsync(options, typeof(string), 7, new Dictionary<string, byte[]?> { ["a"] = [1, 2], ["b"] = null });
        PeerUpdate? seen = null;
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = update =>
        {
            seen = update;
            return true;
        };

        Sut.OnNext(new TestCacheEvent { Id = "1", Source = new Uri("urn:machine"), Data = published, Type = "set" });

        seen.Should().NotBeNull();
        seen!.Version.Should().Be(7);
        seen.Fields["a"].Should().Equal(1, 2);
        seen.Fields["b"].Should().BeNull();
        seen.Metadata.Should().Equal(new Dictionary<string, string?> { ["m"] = "v" });
    }

    [Fact]
    public void An_in_process_peer_update_with_invalid_base64_fails_when_the_fields_are_first_used()
    {
        PeerUpdate? seen = null;
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = update =>
        {
            seen = update;
            return false;
        };

        Sut.OnNext(PeerEvent("set", new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 7L,
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, string?> { ["a"] = "AQI=", ["b"] = "!" },
        }));

        seen.Should().NotBeNull();
        seen!.Fields.Count.Should().Be(2);
        seen.Fields.Invoking(f => f.ToList()).Should().Throw<FormatException>();
    }

    [Fact]
    public void A_decoded_peer_update_the_node_does_not_apply_never_decodes_its_base64()
    {
        var body = """{"Id":"1","Source":"urn:machine","Type":"set","Data":{"Key":"@","Properties":{"_version_":7,"_peerFields_":{"a":"!"}}}}"""u8.ToArray();
        body = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(body).Replace("@", _key));
        var decoded = new CacheEventFormatter().Decode(body)!;
        var handed = false;
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = _ =>
        {
            handed = true;
            return false;
        };

        Sut.Invoking(s => s.OnNext(decoded)).Should().NotThrow();

        handed.Should().BeTrue();
    }

    [Fact]
    public void Two_tokens_built_from_one_event_do_not_share_metadata()
    {
        var other = new ChangeToken<byte[]>(_key, _topic, _source, _serializer, _fixture.Freeze<ILogger<ChangeToken<byte[]>>>(), _telemetryProvider, _acceptedEvents);
        PeerUpdate? first = null;
        PeerUpdate? second = null;
        ((IPeerUpdateSource)Sut).PeerUpdateHandler = update => (first = update) is not null;
        ((IPeerUpdateSource)other).PeerUpdateHandler = update => (second = update) is not null;
        var metadata = new Dictionary<string, string?> { ["m"] = "v" };
        var peerEvent = PeerEvent("set", new Dictionary<string, object?>
        {
            [KnownFieldNames.VersionKey] = 7L,
            [KnownFieldNames.PeerFieldsKey] = new Dictionary<string, byte[]?> { ["a"] = [1] },
            [KnownFieldNames.PeerMetadataKey] = metadata,
        });
        var refreshed = PeerEvent("refreshed", new Dictionary<string, object?> { [KnownFieldNames.MetadataKey] = metadata });

        Sut.OnNext(peerEvent);
        other.OnNext(peerEvent);
        Sut.OnNext(refreshed);
        other.OnNext(refreshed);

        first!.Metadata.Should().NotBeSameAs(metadata).And.NotBeSameAs(second!.Metadata);
        Sut.Metadata.Should().NotBeSameAs(metadata).And.NotBeSameAs(other.Metadata);
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

    private TestCacheEvent PeerEvent(string type, Dictionary<string, object?>? properties = null) =>
        new() { Id = Guid.NewGuid().ToString(), Source = new Uri("urn:machine"), Data = new CacheEventData(_key, properties), Type = type };

    private sealed class RecordingSerializer : ISerializerProxy<byte[]>
    {
        private readonly SystemJsonByteSerializerProxy _inner = new();

        public List<Type> Requested { get; } = [];

        public byte[]? Serialize(object? value) => _inner.Serialize(value);

        public T? Deserialize<T>(byte[]? value) => _inner.Deserialize<T>(value);

        public bool TryDeserialize<T>(string? value, out T? result) => _inner.TryDeserialize(value, out result);

        public bool TryDeserialize<T>(object? value, out T? result)
        {
            Requested.Add(typeof(T));
            return _inner.TryDeserialize(value, out result);
        }
    }

    private sealed class ThrowingSerializer : ISerializerProxy<byte[]>
    {
        public int Calls { get; private set; }

        public byte[]? Serialize(object? value) => Fail<byte[]?>();

        public T? Deserialize<T>(byte[]? value) => Fail<T?>();

        public bool TryDeserialize<T>(string? value, out T? result)
        {
            result = default;
            return Fail<bool>();
        }

        public bool TryDeserialize<T>(object? value, out T? result)
        {
            result = default;
            return Fail<bool>();
        }

        private TResult Fail<TResult>()
        {
            Calls++;
            throw new InvalidOperationException("The value serializer must not see typed event properties.");
        }
    }
}
