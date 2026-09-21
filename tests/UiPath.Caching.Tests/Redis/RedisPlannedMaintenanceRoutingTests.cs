using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only

public class RedisPlannedMaintenanceRoutingTests : IDisposable
{
    private readonly List<RedisPlannedMaintenance> _started = [];
    private readonly MovableClock _clock = new();

    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();
    private readonly IRedisConnector _connector = Substitute.For<IRedisConnector>();

    [Fact]
    public async Task An_announced_disruption_is_recorded_without_reconnecting()
    {
        // The client hands the connection off itself, and probing force-reconnects on a failed write.
        var sut = await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating));

        _connector.DidNotReceive().ForceReconnect();
        sut.InProgress.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Azure_notification_is_recorded_whichever_route_it_arrives_on(bool onCommandConnection)
    {
        // Which connection received the broadcast is not knowable after the fact, so both routes record.
        await StartedAsync();
        var azure = AzureEvent();

        if (onCommandConnection)
        {
            RaiseOnCommandConnection(azure);
        }
        else
        {
            RaiseMaintenance(azure);
        }

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1);
    }

    [Fact]
    public async Task An_Azure_notification_falls_back_to_the_command_connection_when_there_is_no_maintenance_one()
    {
        // The maintenance connection is established in the background and gives up after its retries, so the
        // command connection's copy can be the only one there is.
        await StartedWithoutMaintenanceConnectionAsync();

        RaiseOnCommandConnection(AzureEvent());

        _telemetry.Events.Should().ContainSingle(e => e.Name == "Redis.Maintenance")
            .Which.Properties!["Source"].Should().Be(nameof(AzureMaintenanceEvent));
    }

    [Fact]
    public async Task An_Azure_notification_is_recorded_when_only_the_command_connection_received_it()
    {
        // The channel can go live between publication and delivery, and pub/sub does not replay -- so asking
        // whether the other route is subscribed now would drop the only copy there was.
        await StartedAsync();

        RaiseOnCommandConnection(AzureEvent());

        _telemetry.Events.Should().ContainSingle(e => e.Name == "Redis.Maintenance")
            .Which.Properties!["Source"].Should().Be(nameof(AzureMaintenanceEvent));
    }

    [Fact]
    public async Task A_push_frame_replayed_to_a_rebuilt_connection_is_recorded_once()
    {
        // The client collapses copies only within one multiplexer, and the server replays on reconnect.
        await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1);
    }

    [Fact]
    public async Task Notifications_whose_sequence_could_not_be_read_are_each_recorded()
    {
        // The client reports an unreadable sequence as zero and declines to collapse those itself.
        await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, sequenceId: null));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, sequenceId: null));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2);
    }

    [Fact]
    public async Task Sources_that_are_not_modelled_are_each_recorded_even_when_identical()
    {
        // RawMessage carries no uniqueness contract, and the fallback exists to keep an unknown source visible.
        await StartedAsync();

        RaiseOnCommandConnection(UnmodelledEvent("something we do not model yet"));
        RaiseOnCommandConnection(UnmodelledEvent("something we do not model yet"));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2);
    }

    [Fact]
    public async Task A_replayed_notification_numbered_zero_is_recorded_once()
    {
        // Zero is a legitimate sequence too, so it must not be read as the absent one.
        await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, sequenceId: 0));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, sequenceId: 0));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1);
    }

    [Fact]
    public async Task A_completion_is_not_collapsed_into_the_starter_it_follows()
    {
        // One sequence rather than adjacent ones: on 16 and 17 a key of the sequence alone would pass too.
        await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrated, sequenceId: 16));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2);
    }

    [Fact]
    public async Task A_push_frame_on_the_maintenance_connection_is_ignored()
    {
        // Asserted on the telemetry: nothing here moves health state, so only the record distinguishes the routes.
        await StartedAsync();

        RaiseMaintenance(PushEvent(MaintenanceNotificationType.Moving));

        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.Maintenance");
    }

    [Fact]
    public async Task Every_push_notification_is_recorded_with_its_source()
    {
        await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16));

        var recorded = _telemetry.Events.Should().ContainSingle(e => e.Name == "Redis.Maintenance").Which;
        recorded.Properties!["Source"].Should().Be(nameof(PushMaintenanceEvent));
        recorded.Properties["NotificationTypeString"].Should().Be(nameof(MaintenanceNotificationType.SlotMigrating));
        recorded.Properties["SequenceId"].Should().Be("16");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_source_that_is_not_modelled_is_recorded_rather_than_dropped(bool onCommandConnection)
    {
        // Both routers filter, so both have to be held to this or one can regress alone.
        await StartedAsync();
        var unmodelled = (ServerMaintenanceEvent)Activator.CreateInstance(
            typeof(ServerMaintenanceEvent), BindingFlags.Instance | BindingFlags.NonPublic, null, null, null)!;

        if (onCommandConnection)
        {
            RaiseOnCommandConnection(unmodelled);
        }
        else
        {
            RaiseMaintenance(unmodelled);
        }

        _telemetry.Events.Should().ContainSingle(e => e.Name == "Redis.Maintenance")
            .Which.Properties!["Source"].Should().Be(nameof(ServerMaintenanceEvent));
    }

    [Fact]
    public async Task A_broadcast_taken_on_both_routes_is_recorded_once()
    {
        // Both connections are subscribed to the channel, so both receive the broadcast and both routes record.
        await StartedAsync();
        RaiseOnCommandConnection(AzureEvent());
        RaiseMaintenance(AzureEvent());

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1);
    }

    [Fact]
    public async Task A_broadcast_forwarded_by_two_connection_generations_is_recorded_once()
    {
        // A rebuild leaves both the retired and the replacement connection subscribed, deliberately.
        await StartedWithoutMaintenanceConnectionAsync();

        RaiseOnCommandConnection(AzureEvent());
        RaiseOnCommandConnection(AzureEvent());

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1);
    }

    [Fact]
    public async Task A_different_broadcast_is_recorded_after_a_suppressed_copy()
    {
        await StartedWithoutMaintenanceConnectionAsync();

        RaiseOnCommandConnection(AzureEvent());
        RaiseOnCommandConnection(AzureEvent());
        RaiseOnCommandConnection(AzureEvent("2026-09-19T01:00:00"));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2);
    }

    [Fact]
    public async Task A_copy_is_still_recognised_behind_a_run_of_other_notifications()
    {
        // A set sized by count would drop an identity still inside the window once enough others arrived.
        await StartedWithoutMaintenanceConnectionAsync();

        RaiseOnCommandConnection(AzureEvent());
        for (var i = 0; i < 64; i++)
        {
            RaiseOnCommandConnection(AzureEvent($"2026-09-19T{i / 60:D2}:{i % 60:D2}:30"));
        }

        RaiseOnCommandConnection(AzureEvent());

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(65);
    }

    [Fact]
    public async Task The_retention_window_survives_a_backward_clock_correction()
    {
        // Measured on timestamps, so a host clock correction neither holds entries nor drops the protection.
        await StartedWithoutMaintenanceConnectionAsync();

        RaiseOnCommandConnection(AzureEvent());
        _clock.CorrectWallClock(TimeSpan.FromMinutes(-5));
        _clock.Advance(TimeSpan.FromMinutes(1));
        RaiseOnCommandConnection(AzureEvent());

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2);
    }

    [Fact]
    public async Task A_broadcast_repeated_beyond_the_retention_window_is_recorded_again()
    {
        // Only the copies of one broadcast are collapsed; a later announcement is news however it is worded.
        await StartedWithoutMaintenanceConnectionAsync();

        RaiseOnCommandConnection(AzureEvent());
        _clock.Advance(TimeSpan.FromMinutes(1));
        RaiseOnCommandConnection(AzureEvent());

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2);
    }

    [Fact]
    public async Task Disposing_cancels_the_token_a_connection_attempt_was_given()
    {
        // InitializeAsync catches the cancellation and returns, so the wait ends with the service. It is the
        // token that ends here, not the connect: ConnectionMultiplexerFactory checks it once and then awaits
        // ConnectionMultiplexer.ConnectAsync, which takes none -- a real attempt already in flight runs on.
        // Handed over through a completion source: the worker that creates it and the thread that reads it are
        // different, and a ValueTask is several fields, so polling a shared one can see it half-written.
        var captured = new TaskCompletionSource<Task<IConnectionMultiplexer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379" });
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        // CA2012: arranging a ValueTask-returning member with NSubstitute means calling it and handing the
        // result to Returns. There is no shape of this that awaits it, and nothing ever consumes it.
#pragma warning disable CA2012
        factory.CreateAsync(Arg.Any<ConfigurationOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var attempt = NeverConnects(call.Arg<CancellationToken>());
                captured.TrySetResult(attempt);
                return new ValueTask<IConnectionMultiplexer>(attempt);
            });
#pragma warning restore CA2012
        var sut = new RedisPlannedMaintenance(_telemetry, _connector, new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options), factory, NullLogger<RedisPlannedMaintenance>.Instance, options, null, _clock);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        var pending = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        pending.IsCompleted.Should().BeFalse("the maintenance connection never arrives");
        sut.Dispose();
        var finished = await Task.WhenAny(pending, Task.Delay(5000, TestContext.Current.CancellationToken));
        finished.Should().BeSameAs(pending, "disposing must end the wait rather than leave it suspended");
    }

    [Fact]
    public async Task A_throw_while_handling_a_notification_does_not_escape_the_maintenance_connection()
    {
        // Attached straight to the client, unlike the command route, so nothing else keeps throws off its dispatch.
        var telemetry = new ThrowOnMaintenanceEventProvider();
        await StartedAsync(telemetry);

        var raise = () => RaiseMaintenance(AzureEvent());

        raise.Should().NotThrow("StackExchange.Redis is raising this, and it did not subscribe to our bookkeeping");
        telemetry.Exceptions.Should().ContainSingle().Which.Should().BeSameAs(ThrowOnMaintenanceEventProvider.Failure);
    }

    [Fact]
    public async Task A_report_that_fails_too_does_not_escape_the_maintenance_connection()
    {
        // A sink that cannot take the report leaves nowhere to put it, and it still must not reach the dispatch thread.
        var telemetry = new ThrowOnMaintenanceEventProvider { ReportingThrows = true };
        await StartedAsync(telemetry);

        var raise = () => RaiseMaintenance(AzureEvent());

        raise.Should().NotThrow();
    }

    [Fact]
    public async Task A_notification_whose_recording_threw_is_recorded_when_it_arrives_again()
    {
        // A handler that threw recorded nothing, so holding its claim would lose the notification for the window.
        var telemetry = new ThrowOnMaintenanceEventProvider(failures: 1);
        await StartedAsync(telemetry);
        var azure = AzureEvent();

        RaiseMaintenance(azure);
        RaiseMaintenance(azure);

        telemetry.Exceptions.Should().Contain(ThrowOnMaintenanceEventProvider.Failure);
        telemetry.Events.Count(e => e == "Redis.Maintenance").Should().Be(1, "the retry recorded what the failed attempt did not");
    }

    [Fact]
    public async Task Disposing_reaches_the_multiplexer_when_cancelling_and_reporting_both_throw()
    {
        // Cancel runs the probe loop's registrations, so it can throw what a caller did; the rest of Dispose
        // still has a subscribed connection to let go of.
        var telemetry = new ThrowOnMaintenanceEventProvider { ReportingThrows = true };
        var sut = await StartedAsync(telemetry);
        var source = (CancellationTokenSource)typeof(RedisPlannedMaintenance)
            .GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sut)!;
        source.Token.Register(() => throw new InvalidOperationException("callback boom"));

        var dispose = () => sut.Dispose();

        dispose.Should().NotThrow();
        _multiplexer.Received(1).Dispose();
    }

    [Fact]
    public async Task A_probe_run_whose_start_could_not_be_announced_keeps_probing()
    {
        // Announcing is not what the run is for: losing the event must not cost the probing and the ForceReconnect
        // that are the Azure route's whole recovery.
        var telemetry = new ThrowOnMaintenanceEventProvider { FailingEvent = "Redis.MaintenanceStarted" };
        var sut = await StartedAsync(telemetry);

        RaiseMaintenance(AzureEvent());
        await WaitForRefusalAsync(telemetry);

        // Long enough for the run to have died here, had the refusal ended it.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        sut.InProgress.Should().BeTrue("a refused announcement must not end the probe run");
        telemetry.Events.Should().NotContain("Redis.MaintenanceEnded", "a start that was never announced has no end to announce");
    }

    [Fact]
    public async Task A_probe_run_whose_end_could_not_be_announced_reports_that_rather_than_faulting()
    {
        var telemetry = new ThrowOnMaintenanceEventProvider { FailingEvent = "Redis.MaintenanceEnded" };
        var sut = await StartedAsync(telemetry);

        RaiseMaintenance(AzureEvent());
        for (var i = 0; i < 500 && !telemetry.Events.Contains("Redis.MaintenanceStarted"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        telemetry.Events.Should().Contain("Redis.MaintenanceStarted", "the run announced its start, so it has an end to announce");

        sut.Dispose();
        await WaitForRefusalAsync(telemetry);

        telemetry.Exceptions.Should().Contain(ThrowOnMaintenanceEventProvider.Failure, "the refusal is reported rather than left as an unobserved fault");
    }

    [Fact]
    public async Task Azure_notifications_that_could_not_be_parsed_are_each_recorded()
    {
        // A payload the client cannot parse leaves every field at its default, RawMessage included, so keying on
        // them would collapse two unrelated notifications into one.
        await StartedAsync();

        RaiseMaintenance(AzureEventFrom("garbage"));
        RaiseMaintenance(AzureEventFrom("nonsense"));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(2, "neither carries anything to tell it apart by");
    }

    [Fact]
    public async Task An_Azure_notification_with_an_unrecognised_type_is_still_collapsed()
    {
        // Unknown type but the rest parsed: it has fields to be told apart by, so the claim still holds.
        await StartedAsync();
        const string Payload = "NotificationType|SomethingNew|StartTimeInUTC|2026-09-19T00:00:00|IsReplica|False|IPAddress|127.0.0.1|SSLPort|6380|NonSSLPort|6379";

        RaiseMaintenance(AzureEventFrom(Payload));
        RaiseMaintenance(AzureEventFrom(Payload));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1, "an unrecognised type is not an unparsed payload");
    }

    [Fact]
    public async Task An_Azure_notification_carrying_only_a_new_type_is_still_collapsed()
    {
        // The client keeps the type string it did not recognise, so this parsed -- it is identified by that
        // string alone, where a payload it could not read at all keeps the default.
        await StartedAsync();
        const string Payload = "NotificationType|SomethingNew";

        RaiseMaintenance(AzureEventFrom(Payload));
        RaiseMaintenance(AzureEventFrom(Payload));

        _telemetry.Events.Count(e => e.Name == "Redis.Maintenance").Should().Be(1, "the type string is an identity");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recorded_timestamps_round_trip_whichever_route_carried_them(bool push)
    {
        // One field, one format: a query over Redis.Maintenance should not have to guess which route wrote it.
        await StartedAsync();

        if (push)
        {
            // Push frames are ignored on the maintenance connection, so this route carries them.
            RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, announced: TimeSpan.FromMinutes(5)));
        }
        else
        {
            RaiseMaintenance(AzureEvent());
        }

        var recorded = _telemetry.Events.Should().ContainSingle(e => e.Name == "Redis.Maintenance").Which.Properties!;
        foreach (var field in new[] { "ReceivedTimeUtc", "StartTimeUtc" })
        {
            var value = recorded[field];
            value.Should().NotBeEmpty();
            var parse = () => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture);
            parse.Should().NotThrow($"{field} must be round-trippable");
        }
    }

    /// <summary>Disposes every service started here; a probe loop left running writes through later tests.</summary>
    public void Dispose()
    {
        foreach (var started in _started)
        {
            started.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>An Azure notification built from an arbitrary payload, parsed or not.</summary>
    private static AzureMaintenanceEvent AzureEventFrom(string payload) =>
        (AzureMaintenanceEvent)Activator.CreateInstance(
            typeof(AzureMaintenanceEvent), BindingFlags.Instance | BindingFlags.NonPublic, null, [payload], null)!;

    private static AzureMaintenanceEvent AzureEvent(string startTime = "2026-09-19T00:00:00") =>
        (AzureMaintenanceEvent)Activator.CreateInstance(
            typeof(AzureMaintenanceEvent),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [$"NotificationType|NodeMaintenanceStarting|StartTimeInUTC|{startTime}|IsReplica|False|IPAddress|127.0.0.1|SSLPort|6380|NonSSLPort|6379"],
            null)!;

    /// <summary>A connection attempt that never succeeds, but that ends when the service is disposed.</summary>
    private static Task<IConnectionMultiplexer> NeverConnects(CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => pending.TrySetCanceled(cancellationToken));
        _ = pending.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return pending.Task;
    }

    /// <summary>A notification from a source this library does not model, carrying the given payload.</summary>
    private static ServerMaintenanceEvent UnmodelledEvent(string rawMessage)
    {
        var unmodelled = (ServerMaintenanceEvent)Activator.CreateInstance(
            typeof(ServerMaintenanceEvent), BindingFlags.Instance | BindingFlags.NonPublic, null, null, null)!;
        typeof(ServerMaintenanceEvent).GetProperty(nameof(ServerMaintenanceEvent.RawMessage))!
            .SetValue(unmodelled, rawMessage);
        return unmodelled;
    }

    /// <summary>Null <paramref name="sequenceId"/> is a frame whose sequence could not be read: zero, described as unknown.</summary>
    private static PushMaintenanceEvent PushEvent(MaintenanceNotificationType type, long? sequenceId = 1, TimeSpan? announced = null) =>
        (PushMaintenanceEvent)Activator.CreateInstance(
            typeof(PushMaintenanceEvent),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [
                type,
                sequenceId ?? 0L,
                (EndPoint)new DnsEndPoint("node", 6379),
                announced,
                (EndPoint?)null,
                "payload",
                $"{type.ToString().ToUpperInvariant()} seq={sequenceId?.ToString(CultureInfo.InvariantCulture) ?? "?"} payload",
                Array.Empty<ClusterSlotMigration>(),
            ],
            null)!;

    private static async Task WaitForRefusalAsync(ThrowOnMaintenanceEventProvider telemetry)
    {
        for (var i = 0; i < 500 && telemetry.RefusedAttempts == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        telemetry.RefusedAttempts.Should().Be(1, "the announcement was attempted and refused");
    }

    /// <summary>As the maintenance connection sees it — where Azure's pub/sub copy arrives.</summary>
    private void RaiseMaintenance(ServerMaintenanceEvent e) =>
        _multiplexer.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(_multiplexer, e);

    /// <summary>As the connection carrying commands sees it — the route push frames are taken from.</summary>
    private void RaiseOnCommandConnection(ServerMaintenanceEvent e) =>
        _connector.ServerMaintenance += Raise.Event<EventHandler<ServerMaintenanceEvent>>(_connector, e);

    private async Task<RedisPlannedMaintenance> StartedWithoutMaintenanceConnectionAsync()
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379" });
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        // CA2012: arranging a ValueTask-returning member with NSubstitute means calling it and handing the
        // result to Returns. There is no shape of this that awaits it, and nothing ever consumes it.
#pragma warning disable CA2012
        factory.CreateAsync(Arg.Any<ConfigurationOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<IConnectionMultiplexer>(NeverConnects(call.Arg<CancellationToken>())));
#pragma warning restore CA2012

        var sut = new RedisPlannedMaintenance(
            _telemetry,
            _connector,
            new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options),
            factory,
            NullLogger<RedisPlannedMaintenance>.Instance,
            options,
            null,
            _clock);

        _started.Add(sut);
        await sut.StartAsync(TestContext.Current.CancellationToken);
        return sut;
    }

    private async Task<RedisPlannedMaintenance> StartedAsync(ICachingTelemetryProvider? telemetry = null)
    {
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379" });
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        // CA2012: arranging a ValueTask-returning member with NSubstitute means calling it and handing the
        // result to Returns. There is no shape of this that awaits it, and nothing ever consumes it.
#pragma warning disable CA2012
        factory.CreateAsync(Arg.Any<ConfigurationOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IConnectionMultiplexer>(_multiplexer));
#pragma warning restore CA2012

        var sut = new RedisPlannedMaintenance(
            telemetry ?? _telemetry,
            _connector,
            new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, options),
            factory,
            NullLogger<RedisPlannedMaintenance>.Instance,
            options,
            null,
            _clock);

        _started.Add(sut);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        // StartAsync subscribes on a background task. The multiplexer field is assigned after the handler is
        // attached, so seeing it set means an event raised now will be delivered.
        var field = typeof(RedisPlannedMaintenance).GetField("_multiplexer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 200 && field.GetValue(sut) is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        field.GetValue(sut).Should().NotBeNull("the maintenance subscription must be established before the test raises an event");
        return sut;
    }

    /// <summary>Fails the recording step, the way a telemetry sink under load would.</summary>
    private sealed class ThrowOnMaintenanceEventProvider(int failures = int.MaxValue) : ICachingTelemetryProvider
    {
        public static readonly InvalidOperationException Failure = new("telemetry boom");

        private readonly object _gate = new();
        private readonly List<Exception> _exceptions = [];
        private readonly List<string> _events = [];
        private int _remaining = failures;
        private int _refused;

        /// <summary>Leaves the boundary with nowhere to report.</summary>
        public bool ReportingThrows { get; init; }

        public string FailingEvent { get; init; } = "Redis.Maintenance";

        /// <summary>Counts the attempts that were refused, so a test can wait for one without racing it.</summary>
        public int RefusedAttempts => Volatile.Read(ref _refused);

        public IReadOnlyList<Exception> Exceptions => Snapshot(_exceptions);

        public IReadOnlyList<string> Events => Snapshot(_events);

        public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
        {
            if (eventName == FailingEvent && Interlocked.Decrement(ref _remaining) >= 0)
            {
                Interlocked.Increment(ref _refused);
                throw Failure;
            }

            lock (_gate)
            {
                _events.Add(eventName);
            }
        }

        public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
        {
            if (ReportingThrows)
            {
                throw new InvalidOperationException("sink boom");
            }

            lock (_gate)
            {
                _exceptions.Add(ex);
            }
        }

        private T[] Snapshot<T>(List<T> source)
        {
            lock (_gate)
            {
                return source.ToArray();
            }
        }
    }

    /// <summary>A clock whose timestamps move only when a test moves them, and whose wall reading can disagree.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private long _timestamp = 1_000_000;
        private TimeSpan _wallOffset;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero).AddTicks(_timestamp) + _wallOffset;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;

        /// <summary>What an NTP correction does to the wall clock and not to the timestamps.</summary>
        public void CorrectWallClock(TimeSpan by) => _wallOffset += by;
    }
}

#pragma warning restore SER010
