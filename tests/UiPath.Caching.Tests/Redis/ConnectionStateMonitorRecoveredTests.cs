using UiPath.Caching.Redis;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Redis;

public class ConnectionStateMonitorRecoveredTests
{
    private readonly RecordingTelemetryProvider _telemetry = new();

    [Fact]
    public void A_restore_while_another_state_is_still_down_is_not_a_recovery()
    {
        var inner = Connected();
        var broadcast = Connected();
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, inner, broadcast);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;

        Fail(inner);
        Fail(broadcast);
        Restore(inner);
        recovered.Should().Be(0, "the broadcast connection is still down");

        Restore(broadcast);
        recovered.Should().Be(1);
    }

    [Fact]
    public void A_state_down_at_creation_recovers_on_its_first_restore()
    {
        var inner = Connected();
        inner.IsConnected.Returns(false);
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, inner);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;

        Restore(inner);

        recovered.Should().Be(1);
    }

    [Fact]
    public void One_restore_forwarded_by_two_facades_is_one_recovery()
    {
        var connector = Connected();
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, connector, connector);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;

        Fail(connector);
        Restore(connector);

        recovered.Should().Be(1, "both subscriptions hear the same restore");
    }

    [Fact]
    public void A_forced_reconnect_without_an_outage_is_a_recovery()
    {
        var inner = Connected();
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, inner);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;

        inner.OnReconnected += Raise.Event<EventHandler>(inner, new EventArgs());

        recovered.Should().Be(1, "the swap can drop publications although the connection never read as down");
    }

    [Fact]
    public void Every_forced_reconnect_is_a_recovery_whatever_args_it_carries()
    {
        var inner = Connected();
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, inner);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;
        var reused = new EventArgs();

        inner.OnReconnected += Raise.Event<EventHandler>(inner, reused);
        inner.OnReconnected += Raise.Event<EventHandler>(inner, reused);
        inner.OnReconnected += Raise.Event<EventHandler>(inner, EventArgs.Empty);

        recovered.Should().Be(3, "a source may reuse one args instance, so it says nothing about the swap");
    }

    [Fact]
    public void A_recovery_found_by_polling_is_raised_once()
    {
        var inner = Connected();
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(20), timers, inner);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;

        Fail(inner);
        sut.IsConnected.Should().BeFalse();
        inner.IsConnected.Returns(true);
        timers.Tick();
        timers.Tick();

        recovered.Should().Be(1);
        timers.Armed.Should().Be(0, "the poll stops once recovery holds");
    }

    [Fact]
    public void An_outage_found_only_by_polling_is_seen_to_end()
    {
        var inner = Connected();
        inner.IsConnected.Returns(false);
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(20), timers, inner);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;
        timers.Tick();

        inner.IsConnected.Returns(true);
        timers.Tick();

        recovered.Should().Be(1);
    }

    [Fact]
    public void A_zero_monitor_interval_still_polls_until_recovery()
    {
        var inner = Connected();
        inner.IsConnected.Returns(false);
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.Zero, timers, inner);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;
        sut.IsConnected.Should().BeFalse();

        timers.Period.Should().Be(TimeSpan.FromSeconds(5), "a zero period would fire once and could miss the recovery for good");
        inner.IsConnected.Returns(true);
        timers.Tick();

        recovered.Should().Be(1);
    }

    [Fact]
    public void A_disposed_monitor_stops_polling_however_many_failures_replaced_its_timer()
    {
        var inner = Connected();
        var timers = new ManualTimers();
        var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(10), timers, inner);
        Fail(inner);
        Fail(inner);
        Fail(inner);
        timers.Armed.Should().Be(1, "each failure stops the timer it replaces");

        sut.Dispose();

        timers.Armed.Should().Be(0, "every timer the failures created was stopped");
    }

    [Fact]
    public async Task A_poll_that_sampled_an_outage_before_a_restore_does_not_recover_twice()
    {
        var state = new HeldState();
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(20), timers, state);
        var recovered = 0;
        sut.Recovered += (_, _) => Interlocked.Increment(ref recovered);
        state.Fail();
        var sampling = state.HoldNextRead();
        var poll = Task.Run(timers.Tick, TestContext.Current.CancellationToken);
        sampling.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Should().BeTrue("the poll the failure armed reads the state");

        state.Restore();
        Volatile.Read(ref recovered).Should().Be(1);
        state.Release();
        await poll;
        timers.Tick();

        Volatile.Read(ref recovered).Should().Be(1, "the held poll sampled the outage the restore already ended");
    }

    [Fact]
    public async Task A_poll_whose_healthy_sample_went_stale_keeps_polling()
    {
        var state = new HeldState();
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(20), timers, state);
        var recovered = 0;
        sut.Recovered += (_, _) => Interlocked.Increment(ref recovered);
        state.Fail();
        var sampling = state.HoldNextRead(reading: true);
        var poll = Task.Run(timers.Tick, TestContext.Current.CancellationToken);
        sampling.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Should().BeTrue();

        state.Release();
        await poll;
        Volatile.Read(ref recovered).Should().Be(0, "the final check found the state still down");
        state.SetSilently(up: true);
        timers.Tick();

        Volatile.Read(ref recovered).Should().Be(1);
    }

    [Fact]
    public async Task A_state_down_at_creation_restored_while_its_first_poll_is_held_recovers()
    {
        var state = new HeldState(up: false);
        var sampling = state.HoldNextRead();
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(20), timers, state);
        var recovered = 0;
        sut.Recovered += (_, _) => Interlocked.Increment(ref recovered);
        var poll = Task.Run(timers.Tick, TestContext.Current.CancellationToken);
        sampling.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Should().BeTrue();

        state.Restore();
        state.Release();
        await poll;
        timers.Tick();

        Volatile.Read(ref recovered).Should().Be(1, "the held poll had not recorded its sample, so the restore is the only sign of the outage");
    }

    [Fact]
    public async Task A_state_read_that_sampled_an_outage_before_a_restore_does_not_recover_twice()
    {
        var state = new HeldState();
        using var sut = new ConnectionStateMonitor(_telemetry, Timeout.InfiniteTimeSpan, state);
        var recovered = 0;
        sut.Recovered += (_, _) => Interlocked.Increment(ref recovered);
        state.Fail();
        var sampling = state.HoldNextRead();
        var read = Task.Run(() => sut.IsConnected, TestContext.Current.CancellationToken);
        sampling.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Should().BeTrue();

        state.Restore();
        recovered.Should().Be(1);
        state.Release();
        (await read).Should().BeFalse();
        state.Restore();

        recovered.Should().Be(1, "the held read sampled the outage the restore already ended");
    }

    [Fact]
    public void A_poll_whose_final_check_finds_the_state_down_again_keeps_polling()
    {
        var state = new ScriptedState();
        var timers = new ManualTimers();
        using var sut = new ConnectionStateMonitor(_telemetry, TimeSpan.FromMilliseconds(20), timers, state);
        var recovered = 0;
        sut.Recovered += (_, _) => recovered++;

        // The poll samples twice, then the recovery check samples a third time and finds the state down again.
        state.Fail(readsUntilDown: 2);
        timers.Tick();
        recovered.Should().Be(0);
        timers.Armed.Should().Be(1, "the timer keeps polling until recovery holds");
        timers.Tick();

        recovered.Should().Be(1);
    }

    private static IConnectionState Connected()
    {
        var state = Substitute.For<IConnectionState>();
        state.IsConnected.Returns(true);
        return state;
    }

    private static void Fail(IConnectionState state)
    {
        state.IsConnected.Returns(false);
        state.OnConnectionFailed += Raise.Event<EventHandler>(state, EventArgs.Empty);
    }

    private static void Restore(IConnectionState state)
    {
        state.IsConnected.Returns(true);
        state.OnConnectionRestored += Raise.Event<EventHandler>(state, EventArgs.Empty);
    }

    private int Evaluations() => _telemetry.Events.Count(e => e.Name == "Redis.EvaluateConnected");

    /// <summary>A failed state that reads up a set number of times, then down once, then up for good.</summary>
    private sealed class ScriptedState : IConnectionState
    {
        private int _readsUntilDown = -1;

        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored
        {
            add { }
            remove { }
        }

        public event EventHandler? OnReconnected
        {
            add { }
            remove { }
        }

        public bool IsConnected => Interlocked.Decrement(ref _readsUntilDown) != -1;

        public void Fail(int readsUntilDown)
        {
            Volatile.Write(ref _readsUntilDown, int.MinValue / 2);
            OnConnectionFailed?.Invoke(this, EventArgs.Empty);
            Volatile.Write(ref _readsUntilDown, readsUntilDown);
        }
    }

    /// <summary>A state whose next read can be held mid-sample, reporting a set value, until released.</summary>
    private sealed class HeldState : IConnectionState
    {
        private readonly ManualResetEventSlim _release = new();
        private ManualResetEventSlim? _sampling;
        private bool _reading;
        private volatile bool _up;

        public HeldState(bool up = true) => _up = up;

        public event EventHandler? OnConnectionFailed;

        public event EventHandler? OnConnectionRestored;

        public event EventHandler? OnReconnected;

        public bool IsConnected
        {
            get
            {
                var sampling = Interlocked.Exchange(ref _sampling, null);
                if (sampling is null)
                {
                    return _up;
                }

                sampling.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
                return _reading;
            }
        }

        public ManualResetEventSlim HoldNextRead(bool reading = false)
        {
            _reading = reading;
            var sampling = new ManualResetEventSlim();
            _sampling = sampling;
            return sampling;
        }

        public void Release() => _release.Set();

        public void SetSilently(bool up) => _up = up;

        public void Fail()
        {
            _up = false;
            OnConnectionFailed?.Invoke(this, EventArgs.Empty);
        }

        public void Restore()
        {
            _up = true;
            OnConnectionRestored?.Invoke(this, EventArgs.Empty);
        }

        public void Reconnect() => OnReconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Timers that fire only when a test ticks them, so no poll interleaves with the sequence it drives.</summary>
    private sealed class ManualTimers : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public int Armed
        {
            get
            {
                lock (_timers)
                {
                    return _timers.Count(t => t.IsArmed);
                }
            }
        }

        public TimeSpan Period { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        /// <summary>Fires every armed timer once, as one period elapsing would.</summary>
        public void Tick()
        {
            ManualTimer[] armed;
            lock (_timers)
            {
                armed = [.. _timers.Where(t => t.IsArmed)];
            }

            foreach (var timer in armed)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
        {
            private volatile bool _armed;
            private volatile bool _disposed;

            public bool IsArmed => _armed && !_disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _armed = dueTime != Timeout.InfiniteTimeSpan;
                owner.Period = period;
                return !_disposed;
            }

            public void Fire() => callback(state);

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
