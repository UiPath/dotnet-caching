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

    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly IConnectionMultiplexer _multiplexer = Substitute.For<IConnectionMultiplexer>();
    private readonly IRedisConnector _connector = Substitute.For<IRedisConnector>();
    private readonly AdvanceableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly TaskCompletionSource _connectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IConnectionMultiplexer? _commandConnection;

    [Fact]
    public async Task An_announced_disruption_opens_the_window_without_reconnecting()
    {
        // The client hands the connection off itself, and probing force-reconnects on a failed write.
        var sut = await StartedAsync();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating));

        sut.InProgress.Should().BeTrue();
        _connector.DidNotReceive().ForceReconnect();
        _telemetry.Events.Should().Contain(e => e.Name == "Redis.MaintenanceStarted");
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
    public async Task Only_the_matching_family_closes_its_window()
    {
        // No tail, so only the outstanding failover can hold it open.
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrated));

        sut.InProgress.Should().BeTrue("the failover is still outstanding");
        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.MaintenanceEnded");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver));

        sut.InProgress.Should().BeFalse();
        _telemetry.Events.Should().Contain(e => e.Name == "Redis.MaintenanceEnded");
    }

    [Fact]
    public async Task An_interval_whose_start_was_refused_announces_no_end()
    {
        var telemetry = new ThrowOnMaintenanceEventProvider { FailingEvent = "Redis.MaintenanceStarted" };
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0", telemetry);

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver));
        sut.InProgress.Should().BeTrue("the window is open whether or not the start could be announced");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver));

        sut.InProgress.Should().BeFalse();
        telemetry.Events.Should().NotContain("Redis.MaintenanceEnded", "no start was ever announced");
    }

    [Fact]
    public async Task The_Azure_probe_route_suggests_no_timeout()
    {
        var sut = await StartedAsync();

        RaiseMaintenance(AzureEvent());

        sut.InProgress.Should().BeTrue("the probe loop is running");
        sut.SuggestedTimeout.Should().BeNull("the connection is not relaxing its own timeouts on this route");
    }

    [Fact]
    public async Task An_announced_window_suggests_what_the_client_relaxes_to()
    {
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver));

        sut.InProgress.Should().BeTrue();
        sut.SuggestedTimeout.Should().NotBeNull("this route is one the client relaxes its own timeouts for");
    }

    [Fact]
    public async Task A_window_is_sized_by_the_connection_it_arrived_on()
    {
        // Raised while this service's own connect is still inside the configurator pass.
        var sut = await StartedWithGatedConfiguratorAsync(TimeSpan.FromSeconds(30));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver), DeliveredBy("localhost:6379,asyncTimeout=500,maintRelaxedTimeout=1,maintPostEventRelaxed=0"));

        sut.InProgress.Should().BeTrue();
        sut.SuggestedTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_connection_is_resized_by_its_own_bounds_after_another_was_adopted()
    {
        var sut = await StartedAsync();
        var first = DeliveredBy("localhost:6379,maintRelaxedTimeout=1,maintPostEventRelaxed=0");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver, sequenceId: 1), first);
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver, sequenceId: 2), DeliveredBy("localhost:6379,maintRelaxedTimeout=60,maintPostEventRelaxed=0"));
        sut.SuggestedTimeout.Should().Be(TimeSpan.FromSeconds(60));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver, sequenceId: 3), first);

        sut.SuggestedTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Notices_from_two_connections_are_handled_one_at_a_time()
    {
        // The first notice is held inside its record while the second arrives.
        var telemetry = new BlockingFirstRecordProvider();
        await StartedAsync(telemetry: telemetry);
        var first = Task.Run(() => RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, sequenceId: 1), DeliveredBy("localhost:6379,maintRelaxedTimeout=1")), TestContext.Current.CancellationToken);
        await telemetry.Blocked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var second = Task.Run(() => RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver, sequenceId: 2), DeliveredBy("localhost:6379,maintRelaxedTimeout=60")), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        second.IsCompleted.Should().BeFalse("the second notice waits for the first to finish");
        telemetry.Release();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_maintenance_connection_does_not_resize_a_window_its_sender_sized()
    {
        var sut = await StartedWithGatedConfiguratorAsync(TimeSpan.FromSeconds(60));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating), DeliveredBy("localhost:6379,maintRelaxedTimeout=1,maintPostEventRelaxed=0"));

        _connectGate.TrySetResult();
        await WaitForConnectedAsync(sut);

        sut.SuggestedTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_replay_from_a_retired_connection_does_not_resize_the_window()
    {
        var sut = await StartedAsync();
        var frame = PushEvent(MaintenanceNotificationType.Migrating, sequenceId: 7);
        RaiseOnCommandConnection(frame, DeliveredBy("localhost:6379,maintRelaxedTimeout=60,maintPostEventRelaxed=0"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        RaiseOnCommandConnection(frame, DeliveredBy("localhost:6379,maintRelaxedTimeout=1,maintPostEventRelaxed=0"));

        sut.InProgress.Should().BeTrue();
        sut.SuggestedTimeout.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_window_is_not_cut_short_before_this_service_connects()
    {
        // Sized by the client default, it would lapse at 10s.
        var sut = await StartedWithGatedConfiguratorAsync(TimeSpan.FromSeconds(60));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating), DeliveredBy("localhost:6379,maintRelaxedTimeout=60,maintPostEventRelaxed=0"));
        _clock.Advance(TimeSpan.FromSeconds(15));
        _connectGate.TrySetResult();
        await WaitForConnectedAsync(sut);

        sut.InProgress.Should().BeTrue();
        _telemetry.Events.Count(e => e.Name == "Redis.MaintenanceStarted").Should().Be(1);
        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.MaintenanceEnded");
    }

    [Fact]
    public async Task A_configuration_that_will_not_parse_keeps_the_bounds_and_the_notice()
    {
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver), DeliveredBy("localhost:6379,notAKnownKey=1"));

        sut.InProgress.Should().BeTrue();
        _telemetry.Events.Should().Contain(e => e.Name == "Redis.Maintenance");
    }

    [Fact]
    public async Task A_probe_rejected_while_disconnected_leaves_the_client_to_reconnect()
    {
        _connector.IsConnected.Returns(false);
        FailProbes(ConnectionFailureType.UnableToConnect);
        await StartedAsync();

        RaiseMaintenance(AzureEvent());
        await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);

        _connector.DidNotReceive().ForceReconnect();
    }

    [Fact]
    public async Task A_disconnect_that_outlasts_the_hanging_time_still_forces_a_reconnect()
    {
        _connector.IsConnected.Returns(false);
        FailProbes(ConnectionFailureType.UnableToConnect);
        await StartedAsync();

        RaiseMaintenance(AzureEvent());
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(11));

        await WaitForAsync(() => _connector.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IRedisConnector.ForceReconnect)));
    }

    [Fact]
    public async Task A_probe_failing_while_connected_forces_a_reconnect_at_once()
    {
        _connector.IsConnected.Returns(true);
        FailProbes(ConnectionFailureType.SocketFailure);
        await StartedAsync();

        RaiseMaintenance(AzureEvent());

        await WaitForAsync(() => _connector.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IRedisConnector.ForceReconnect)));
    }

    [Fact]
    public async Task A_frame_replayed_later_in_a_long_window_is_still_collapsed()
    {
        var sut = await StartedAsync("localhost:6379,maintRelaxedTimeout=60,maintRelaxedWindowMax=120,maintPostEventRelaxed=0");
        var starter = PushEvent(MaintenanceNotificationType.FailingOver, announced: TimeSpan.FromSeconds(100));

        RaiseOnCommandConnection(starter);
        _clock.Advance(TimeSpan.FromSeconds(45));
        RaiseOnCommandConnection(starter);

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver));

        sut.InProgress.Should().BeFalse("the replay was collapsed, so one completion closes the one operation");
    }

    [Fact]
    public async Task Every_live_completion_earns_a_tail_not_just_the_last()
    {
        // The first completion's tail outlasts the second operation.
        var sut = await StartedAsync("localhost:6379,maintRelaxedTimeout=10,maintRelaxedWindowMax=200,maintPostEventRelaxed=60");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver, sequenceId: 1, announced: TimeSpan.FromSeconds(20)));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver, sequenceId: 2, announced: TimeSpan.FromSeconds(30)));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver, sequenceId: 3));
        _clock.Advance(TimeSpan.FromSeconds(40));

        sut.InProgress.Should().BeTrue("the completed operation's 60s tail outlives the 30s one still running");
    }

    [Fact]
    public async Task The_window_maximum_caps_a_relaxed_timeout_above_it()
    {
        var sut = await StartedAsync("localhost:6379,maintRelaxedTimeout=10,maintRelaxedWindowMax=1,maintPostEventRelaxed=0");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, announced: TimeSpan.FromSeconds(2)));
        sut.InProgress.Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(2));

        sut.InProgress.Should().BeFalse("1s is the documented cap, whatever the floor says");
    }

    [Fact]
    public async Task A_refused_record_does_not_release_the_claim_that_opened_a_window()
    {
        var telemetry = new ThrowOnMaintenanceEventProvider { FailingEvent = "Redis.Maintenance" };
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0", telemetry);
        var moving = PushEvent(MaintenanceNotificationType.FailingOver);

        RaiseOnCommandConnection(moving);
        RaiseOnCommandConnection(moving);

        sut.InProgress.Should().BeTrue("the window is open");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver));

        sut.InProgress.Should().BeFalse("one completion closes the one operation the replay must not have doubled");
    }

    [Fact]
    public async Task A_window_is_resized_when_its_connection_brings_longer_bounds()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating), DeliveredBy("localhost:6379,maintRelaxedTimeout=10,maintPostEventRelaxed=0"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        RaiseOnCommandConnection(UnmodelledEvent("longer bounds"), DeliveredBy("localhost:6379,maintRelaxedTimeout=60,maintPostEventRelaxed=0"));
        _clock.Advance(TimeSpan.FromSeconds(30));

        sut.InProgress.Should().BeTrue("60s is what the connection now relaxes to, so the open window runs to that");
    }

    [Fact]
    public async Task Shortened_bounds_close_an_open_window_at_once_rather_than_at_the_old_deadline()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating), DeliveredBy("localhost:6379,maintRelaxedTimeout=10,maintPostEventRelaxed=0"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        RaiseOnCommandConnection(UnmodelledEvent("shorter bounds"), DeliveredBy("localhost:6379,maintRelaxedTimeout=1,maintPostEventRelaxed=0"));

        sut.InProgress.Should().BeFalse("1s is what the connection now relaxes to, and 5s have passed");
        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name == "Redis.MaintenanceEnded"));
    }

    [Fact]
    public async Task Stopping_does_not_announce_an_interval_a_pending_probe_left_behind()
    {
        // Stopped before the queued probe worker runs, with the flag set and nothing announced.
        var sut = await StartedAsync();
        sut.InProgress = true;

        await sut.StopAsync(TestContext.Current.CancellationToken);

        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.MaintenanceStarted");
        _telemetry.Events.Should().NotContain(e => e.Name == "Redis.MaintenanceEnded");
    }

    [Fact]
    public async Task A_wall_clock_correction_forward_does_not_close_an_open_window()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, announced: TimeSpan.FromSeconds(30)));

        _clock.CorrectWallClock(TimeSpan.FromMinutes(10));

        sut.InProgress.Should().BeTrue("no time has actually elapsed, whatever the wall clock now reads");
    }

    [Fact]
    public async Task A_wall_clock_correction_backward_does_not_hold_a_window_past_its_duration()
    {
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, announced: TimeSpan.FromSeconds(30)));

        _clock.CorrectWallClock(TimeSpan.FromMinutes(-10));
        _clock.Advance(TimeSpan.FromSeconds(31));

        sut.InProgress.Should().BeFalse("30 seconds elapsed however far back the wall clock was set");
    }

    [Fact]
    public async Task A_shorter_overlapping_operation_does_not_shorten_a_longer_one()
    {
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16, announced: TimeSpan.FromSeconds(30)));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 18, announced: TimeSpan.FromSeconds(5)));

        _clock.Advance(TimeSpan.FromSeconds(15));

        sut.InProgress.Should().BeTrue("the 30s migration is still running, whatever the later one announced");
    }

    [Fact]
    public async Task A_late_completion_does_not_release_a_longer_operation_still_running()
    {
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0,maintRelaxedTimeout=5,maintRelaxedWindowMax=120");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16, announced: TimeSpan.FromSeconds(60)));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 18, announced: TimeSpan.FromSeconds(2)));
        _clock.Advance(TimeSpan.FromSeconds(10));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver, sequenceId: 20));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrated, sequenceId: 19));

        sut.InProgress.Should().BeTrue("the completion may be the lapsed operation's, and the 60s one is still running");
    }

    [Fact]
    public async Task A_replay_after_its_claim_aged_out_does_not_add_to_a_family_still_running()
    {
        // Starters at 0s and 30s keep the family alive past the first claim at 60s.
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0,maintRelaxedTimeout=5,maintRelaxedWindowMax=60");
        var first = PushEvent(MaintenanceNotificationType.Migrating, sequenceId: 1, announced: TimeSpan.FromSeconds(60));
        RaiseOnCommandConnection(first);
        _clock.Advance(TimeSpan.FromSeconds(30));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, sequenceId: 2, announced: TimeSpan.FromSeconds(60)));
        _clock.Advance(TimeSpan.FromSeconds(31));

        RaiseOnCommandConnection(first);
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrated, sequenceId: 3));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrated, sequenceId: 4));

        sut.InProgress.Should().BeFalse("two operations ran and two completed; the replay added none");
    }

    [Fact]
    public async Task Overlapping_operations_of_one_family_each_hold_the_window()
    {
        // A completion has its own sequence id, so nothing pairs it with a starter.
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 16));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrating, sequenceId: 18));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrated, sequenceId: 17));

        sut.InProgress.Should().BeTrue("the second migration is still running");

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.SlotMigrated, sequenceId: 19));

        sut.InProgress.Should().BeFalse("both have completed");
    }

    [Fact]
    public async Task A_completion_hands_over_to_the_tail_the_client_stays_relaxed_for()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrated));

        sut.InProgress.Should().BeTrue("the client is still relaxed for maintPostEventRelaxed");

        _clock.Advance(TimeSpan.FromSeconds(21));

        sut.InProgress.Should().BeFalse();
    }

    [Fact]
    public async Task A_completion_arriving_after_its_window_lapsed_does_not_reopen_it()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, announced: TimeSpan.FromSeconds(15)));

        // Without firing the timer, so the lapsed entry is still present.
        _clock.AdvanceWithoutFiringTimers(TimeSpan.FromSeconds(16));
        sut.InProgress.Should().BeFalse();

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrated));

        sut.InProgress.Should().BeFalse("a lapsed window is over, and its completion cannot restart it");
        _telemetry.Events.Count(e => e.Name == "Redis.MaintenanceStarted").Should().Be(1, "one disruption is one interval");
    }

    [Fact]
    public async Task A_new_operation_does_not_discard_the_tail_an_earlier_one_earned()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating));
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrated));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating));
        _clock.Advance(TimeSpan.FromSeconds(15));

        sut.InProgress.Should().BeTrue("the 20s tail outlives the 10s window the new operation opened");
    }

    [Fact]
    public async Task A_completion_closes_at_once_when_there_is_no_tail()
    {
        var sut = await StartedAsync("localhost:6379,maintPostEventRelaxed=0");
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailingOver));

        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.FailedOver));

        sut.InProgress.Should().BeFalse("nothing is left relaxed, so nothing is left to suppress");
    }

    [Fact]
    public async Task A_window_whose_completion_never_arrives_lapses()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Moving, announced: TimeSpan.FromSeconds(15)));
        sut.InProgress.Should().BeTrue();

        _clock.Advance(TimeSpan.FromSeconds(16));

        sut.InProgress.Should().BeFalse();
        await WaitForAsync(() => _telemetry.Events.Any(e => e.Name == "Redis.MaintenanceEnded"));
    }

    [Fact]
    public async Task An_announced_duration_shorter_than_a_reconnect_is_widened()
    {
        var sut = await StartedAsync();
        RaiseOnCommandConnection(PushEvent(MaintenanceNotificationType.Migrating, announced: TimeSpan.FromSeconds(2)));

        _clock.Advance(TimeSpan.FromSeconds(5));

        sut.InProgress.Should().BeTrue();
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
        await StartedAsync(telemetry: telemetry);

        var raise = () => RaiseMaintenance(AzureEvent());

        raise.Should().NotThrow("StackExchange.Redis is raising this, and it did not subscribe to our bookkeeping");
        telemetry.Exceptions.Should().ContainSingle().Which.Should().BeSameAs(ThrowOnMaintenanceEventProvider.Failure);
    }

    [Fact]
    public async Task A_report_that_fails_too_does_not_escape_the_maintenance_connection()
    {
        // A sink that cannot take the report leaves nowhere to put it, and it still must not reach the dispatch thread.
        var telemetry = new ThrowOnMaintenanceEventProvider { ReportingThrows = true };
        await StartedAsync(telemetry: telemetry);

        var raise = () => RaiseMaintenance(AzureEvent());

        raise.Should().NotThrow();
    }

    [Fact]
    public async Task A_refused_record_is_reported_and_not_retried()
    {
        var telemetry = new ThrowOnMaintenanceEventProvider(failures: 1);
        await StartedAsync(telemetry: telemetry);
        var azure = AzureEvent();

        RaiseMaintenance(azure);
        RaiseMaintenance(azure);

        telemetry.Exceptions.Should().Contain(ThrowOnMaintenanceEventProvider.Failure, "the refusal is reported");
        telemetry.Events.Should().NotContain("Redis.Maintenance", "the copy is collapsed, so nothing re-records it");
    }

    [Fact]
    public async Task Disposing_reaches_the_multiplexer_when_cancelling_and_reporting_both_throw()
    {
        // Cancel runs the probe loop's registrations, so it can throw what a caller did; the rest of Dispose
        // still has a subscribed connection to let go of.
        var telemetry = new ThrowOnMaintenanceEventProvider { ReportingThrows = true };
        var sut = await StartedAsync(telemetry: telemetry);
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
        var sut = await StartedAsync(telemetry: telemetry);

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
        var sut = await StartedAsync(telemetry: telemetry);

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
    private static async Task WaitForConnectedAsync(RedisPlannedMaintenance sut)
    {
        // Assigned once the connect has finished.
        var field = typeof(RedisPlannedMaintenance).GetField("_multiplexer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var i = 0; i < 200 && field.GetValue(sut) is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        field.GetValue(sut).Should().NotBeNull();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // The delay continuation runs off the advancing thread.
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue("the lapsed window must record its end, not just stop reporting");
    }

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

    /// <summary>A command connection built with <paramref name="configuration"/>, as the connector reports it.</summary>
    private static IConnectionMultiplexer DeliveredBy(string configuration)
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.Configuration.Returns(configuration);
        return multiplexer;
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
        // The count rises inside the throw, before the catch has reported it.
        for (var i = 0; i < 500 && (telemetry.RefusedAttempts == 0 || telemetry.Exceptions.Length == 0); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        telemetry.RefusedAttempts.Should().Be(1, "the announcement was attempted and refused");
    }

    /// <summary>As the maintenance connection sees it — where Azure's pub/sub copy arrives.</summary>
    private void RaiseMaintenance(ServerMaintenanceEvent e) =>
        _multiplexer.ServerMaintenanceEvent += Raise.Event<EventHandler<ServerMaintenanceEvent>>(_multiplexer, e);

    /// <summary>As the connection carrying commands sees it — the route push frames are taken from.</summary>
    private void RaiseOnCommandConnection(ServerMaintenanceEvent e, object? sender = null) =>
        _connector.ServerMaintenance += Raise.Event<EventHandler<ServerMaintenanceEvent>>(sender ?? _commandConnection ?? (object)_connector, e);

    /// <summary>The command connection the connector would build from <paramref name="connectionString"/>.</summary>
    private void UseCommandConnection(string connectionString)
    {
        var provider = new RedisConfigurationOptionsProvider(NullLoggerFactory.Instance, Options.Create(new RedisConnectionOptions { ConnectionString = connectionString }));
        var configuration = provider.GetConfiguration();
        provider.ReapplyDerivedBounds(configuration);
        _commandConnection = DeliveredBy(configuration.ToString());
    }

    /// <summary>Fails every probe write, whichever overload the probe binds to.</summary>
    private void FailProbes(ConnectionFailureType failure)
    {
        Task<bool> Fail() => Task.FromException<bool>(new RedisConnectionException(failure, CommandFlags.None, "probe"));
        _connector.Database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>()).Returns(_ => Fail());
        _connector.Database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>()).Returns(_ => Fail());
        _connector.Database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(_ => Fail());
        _connector.Database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>()).Returns(_ => Fail());
    }

    private async Task<RedisPlannedMaintenance> StartedWithoutMaintenanceConnectionAsync()
    {
        UseCommandConnection("localhost:6379");
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = "localhost:6379" });
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        // CA2012: arranging a ValueTask-returning member with NSubstitute means calling it and handing the
        // result to Returns. There is no shape of this that awaits it, and nothing ever consumes it.
#pragma warning disable CA2012
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.CreateAsync(Arg.Any<ConfigurationOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                creating.TrySetResult();
                return new ValueTask<IConnectionMultiplexer>(NeverConnects(call.Arg<CancellationToken>()));
            });
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

        // Created once the connect has passed the configurators.
        await creating.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        return sut;
    }

    private async Task<RedisPlannedMaintenance> StartedWithGatedConfiguratorAsync(TimeSpan relaxedTimeout, string connectionString = "localhost:6379", ICachingTelemetryProvider? telemetry = null)
    {
        UseCommandConnection(connectionString);
        var configurator = new RelaxedTimeoutConfigurator(relaxedTimeout, _connectGate.Task);
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = connectionString });
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
            [configurator],
            _clock);

        _started.Add(sut);
        await sut.StartAsync(TestContext.Current.CancellationToken);
        return sut;
    }

    private async Task<RedisPlannedMaintenance> StartedAsync(string connectionString = "localhost:6379", ICachingTelemetryProvider? telemetry = null)
    {
        UseCommandConnection(connectionString);
        var options = Options.Create(new RedisConnectionOptions { ConnectionString = connectionString });
        var factory = Substitute.For<IConnectionMultiplexerFactory>();
        // CA2012: arranging a ValueTask-returning member with NSubstitute means calling it and handing the
        // result to Returns. There is no shape of this that awaits it, and nothing ever consumes it.
#pragma warning disable CA2012
        factory.CreateAsync(Arg.Any<ConfigurationOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IConnectionMultiplexer>(_connectGate.Task.ContinueWith(_ => _multiplexer, TaskScheduler.Default)));
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
        _connectGate.TrySetResult();

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
    /// <summary>Holds the first <c>Redis.Maintenance</c> record until released.</summary>
    private sealed class BlockingFirstRecordProvider : ICachingTelemetryProvider
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _release = new();
        private int _records;

        public Task Blocked => _blocked.Task;

        public void Release() => _release.Set();

        public void TrackEvent(string eventName, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
        {
            if (eventName == "Redis.Maintenance" && Interlocked.Increment(ref _records) == 1)
            {
                _blocked.TrySetResult();
                _release.Wait(TimeSpan.FromSeconds(10));
            }
        }

        public void TrackException(Exception ex, ReadOnlySpan<KeyValuePair<string, string>> properties = default, ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
        {
        }
    }

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

        public Exception[] Exceptions => Snapshot(_exceptions);

        public string[] Events => Snapshot(_events);

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

}

#pragma warning restore SER010

/// <summary>A clock a test moves; its timers fire on an advance.</summary>
internal sealed class AdvanceableClock(DateTimeOffset now) : TimeProvider
{
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = now;
    private TimeSpan _wallOffset;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_timers)
        {
            return _now + _wallOffset;
        }
    }

    public override long GetTimestamp()
    {
        lock (_timers)
        {
            return _now.UtcTicks;
        }
    }

    /// <summary>Moves the wall clock only.</summary>
    public void CorrectWallClock(TimeSpan by)
    {
        lock (_timers)
        {
            _wallOffset += by;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Schedule(dueTime);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        FakeTimer[] due;
        lock (_timers)
        {
            _now = _now.Add(by);
            due = _timers.Where(timer => timer.IsDue(_now.UtcTicks)).ToArray();
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    /// <summary>Moves time without running the timers now due.</summary>
    public void AdvanceWithoutFiringTimers(TimeSpan by)
    {
        lock (_timers)
        {
            _now = _now.Add(by);
        }
    }

    internal void Forget(FakeTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }

    internal sealed class FakeTimer(AdvanceableClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long? _dueTicks;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime);
            return true;
        }

        public void Dispose() => clock.Forget(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        // Monotonic, so a wall-clock correction cannot move an armed timer.
        internal void Schedule(TimeSpan dueTime) =>
            _dueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetTimestamp() + dueTime.Ticks;

        internal bool IsDue(long nowTicks) => _dueTicks is { } due && due <= nowTicks;

        internal void Fire()
        {
            _dueTicks = null;
            callback(state);
        }
    }
}

/// <summary>Sets the relaxed timeout once the gate opens.</summary>
internal sealed class RelaxedTimeoutConfigurator(TimeSpan relaxedTimeout, Task gate) : IRedisConnectionConfigurator
{
    public async ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
    {
        await gate.ConfigureAwait(false);
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        configuration.MaintenanceRelaxedTimeout = relaxedTimeout;
#pragma warning restore SER010
    }
}
