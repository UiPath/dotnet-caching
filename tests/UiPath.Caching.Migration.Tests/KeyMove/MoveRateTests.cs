using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

public sealed class MoveRateTests
{
    private const string EvictionPause = "evicted";
    private const string OomPause = "oom_backoff";
    private const string SchedulePause = "schedule";

    private static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_unit_starts_at_100_keys_and_1_MiB_per_second()
    {
        var rig = new Rig();

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();

        rig.Rate.RateOf("u1").Should().Be((100d, 1024d * 1024));
        rig.Rate.RateOf("never-seen").Should().Be((100d, 1024d * 1024));
    }

    [Fact]
    public async Task The_rate_rises_ten_percent_every_60_seconds_while_green_and_not_before()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);

        for (var s = 10; s < 60; s += 10)
        {
            rig.Move("u1");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
            rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100, $"{s + 10} seconds is short of the interval");
        }

        rig.Move("u1");
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Rate.RateOf("u1").KeysPerSecond.Should().BeApproximately(110, 1e-9);
        rig.Rate.RateOf("u1").BytesPerSecond.Should().BeApproximately(1.1 * 1024 * 1024, 1e-3);

        rig.Move("u1");
        rig.Time.Advance(TimeSpan.FromSeconds(60));
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Rate.RateOf("u1").KeysPerSecond.Should().BeApproximately(121, 1e-9);
    }

    [Fact]
    public async Task Amber_halves_both_rates_at_each_reading_down_to_the_floor_and_restarts_the_increase_clock()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Health.State = KeyMoveHealthState.Amber;

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull("amber slows the mover, it does not stop it");
        rig.Rate.RateOf("u1").Should().Be((50d, 512d * 1024));
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Rate.RateOf("u1").Should().Be((25d, 256d * 1024));

        for (var i = 0; i < 20; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        rig.Rate.RateOf("u1").Should().Be((1d, 16d * 1024), "the floor");

        // Green again: the next increase is a full interval after the last amber.
        rig.Health.State = KeyMoveHealthState.Green;
        for (var i = 0; i < 5; i++)
        {
            rig.Move("u1");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(1);
        rig.Move("u1");
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Rate.RateOf("u1").KeysPerSecond.Should().BeApproximately(1.1, 1e-9);
    }

    [Fact]
    public async Task Red_pauses_with_the_reason_red_and_green_resumes_at_the_rate_the_unit_had()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Health.State = KeyMoveHealthState.Red;

        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be("red");
        (await rig.Rate.PauseReasonAsync("u2", Ct)).Should().Be("red", "the pause is the node's");
        rig.Time.Advance(TimeSpan.FromSeconds(70));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be("red");
        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100, "a pause does not raise the rate");

        rig.Health.State = KeyMoveHealthState.Green;
        rig.Time.Advance(TimeSpan.FromSeconds(10));

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100);
    }

    [Fact]
    public async Task A_health_source_that_throws_counts_as_red()
    {
        var rig = new Rig();
        rig.Health.Throw = true;

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be("red");

        rig.Health.Throw = false;
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Memory_that_cannot_be_read_pauses_the_mover_and_never_lets_it_run_blind()
    {
        var rig = new Rig();
        rig.Memory.Throw = true;

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be("red");

        rig.Memory.Throw = false;
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task The_health_is_read_once_per_evaluation_interval_however_often_the_units_ask()
    {
        var rig = new Rig();

        for (var i = 0; i < 50; i++)
        {
            await rig.Rate.PauseReasonAsync("u" + (i % 3), Ct);
        }

        rig.Health.Reads.Should().Be(1);
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Health.Reads.Should().Be(2);
    }

    [Fact]
    public void The_global_cap_holds_across_units()
    {
        var rig = new Rig(o => o.GlobalKeysPerSecond = 100);

        // Three units at 100 keys per second each would finish their batches of 100 together in one second; the cap of the node lets one batch of 100 through each second.
        rig.Rate.Reserve("u1", 100, 0).Should().Be(TimeSpan.FromSeconds(1));
        rig.Rate.Reserve("u2", 100, 0).Should().Be(TimeSpan.FromSeconds(2));
        rig.Rate.Reserve("u3", 100, 0).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Without_a_cap_each_unit_keeps_to_its_own_rate_and_the_bytes_limit_counts_too()
    {
        var rig = new Rig();

        rig.Rate.Reserve("u1", 100, 0).Should().Be(TimeSpan.FromSeconds(1));
        rig.Rate.Reserve("u2", 100, 0).Should().Be(TimeSpan.FromSeconds(1));
        rig.Rate.Reserve("u3", 10, 3L * 1024 * 1024).Should().Be(TimeSpan.FromSeconds(3), "3 MiB at 1 MiB per second outlasts 10 keys at 100 per second");
    }

    [Fact]
    public void The_global_bytes_cap_holds_across_units()
    {
        var rig = new Rig(o => o.GlobalBytesPerSecond = 1024 * 1024);
        const long Half = 512 * 1024;

        rig.Rate.Reserve("u1", 1, Half).Should().Be(TimeSpan.FromSeconds(0.5));
        rig.Rate.Reserve("u2", 1, Half).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_cap_is_read_live()
    {
        var rig = new Rig();
        rig.Rate.Reserve("u1", 100, 0).Should().Be(TimeSpan.FromSeconds(1));

        rig.Options.GlobalKeysPerSecond = 10;

        rig.Rate.Reserve("u2", 100, 0).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Outside_the_window_the_mover_pauses_for_the_schedule_and_inside_it_runs()
    {
        // Every day from 22:00 to 06:00 UTC; the clock starts at noon.
        var rig = new Rig(o => o.Windows = [new MoveWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) }]);

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(SchedulePause);
        rig.Time.Advance(TimeSpan.FromHours(11));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull("23:00");
        rig.Time.Advance(TimeSpan.FromHours(4));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull("03:00 the next day");
        rig.Time.Advance(TimeSpan.FromHours(3));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(SchedulePause, "06:00");
    }

    [Theory]
    [InlineData(12, 0, DayOfWeek.Wednesday, true)]
    [InlineData(8, 59, DayOfWeek.Wednesday, false)]
    [InlineData(17, 0, DayOfWeek.Wednesday, false)]
    [InlineData(12, 0, DayOfWeek.Thursday, false)]
    public void A_window_has_a_start_an_end_that_is_not_in_it_and_days(int hour, int minute, DayOfWeek day, bool inside)
    {
        var window = new MoveWindow { Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(17), Days = [DayOfWeek.Tuesday, DayOfWeek.Wednesday] };
        var at = new DateTimeOffset(2026, 3, 4, hour, minute, 0, TimeSpan.Zero); // 2026-03-04 is a Wednesday
        at = at.AddDays((int)day - (int)DayOfWeek.Wednesday);

        window.Contains(at).Should().Be(inside);
    }

    [Theory]
    [InlineData(10, 0, false)] // 08:00 UTC
    [InlineData(11, 0, true)] // 09:00 UTC
    [InlineData(18, 59, true)] // 16:59 UTC
    [InlineData(19, 0, false)] // 17:00 UTC
    public void A_window_is_judged_in_UTC_whatever_offset_the_clock_has(int hour, int minute, bool inside)
    {
        var window = new MoveWindow { Start = TimeSpan.FromHours(9), End = TimeSpan.FromHours(17) };

        window.Contains(new DateTimeOffset(2026, 3, 4, hour, minute, 0, TimeSpan.FromHours(2))).Should().Be(inside);
    }

    [Fact]
    public void A_window_takes_the_day_of_week_from_UTC_not_from_the_offset()
    {
        // 01:00 at +02:00 on Thursday 2026-03-05 is 23:00 UTC on Wednesday.
        var window = new MoveWindow { Start = TimeSpan.FromHours(22), End = new TimeSpan(23, 59, 0), Days = [DayOfWeek.Wednesday] };
        var at = new DateTimeOffset(2026, 3, 5, 1, 0, 0, TimeSpan.FromHours(2));

        window.Contains(at).Should().BeTrue();
    }

    [Fact]
    public void A_window_past_midnight_belongs_to_the_day_it_opened_on()
    {
        var window = new MoveWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6), Days = [DayOfWeek.Friday] };

        window.Contains(new DateTimeOffset(2026, 3, 6, 23, 0, 0, TimeSpan.Zero)).Should().BeTrue("Friday 23:00");
        window.Contains(new DateTimeOffset(2026, 3, 7, 3, 0, 0, TimeSpan.Zero)).Should().BeTrue("Saturday 03:00 is the end of Friday's window");
        window.Contains(new DateTimeOffset(2026, 3, 6, 3, 0, 0, TimeSpan.Zero)).Should().BeFalse("Friday 03:00 is the end of Thursday's");
    }

    [Theory]
    [InlineData("memory_ceiling")]
    [InlineData("memory_growth")]
    [InlineData(EvictionPause)]
    [InlineData("noeviction")]
    public async Task A_memory_rule_pauses_with_its_own_reason_and_lifts_with_it_whatever_the_health_says(string reason)
    {
        var rig = new Rig();
        rig.Memory.Reason = reason;

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(reason);

        rig.Memory.Reason = null;
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_memory_pause_does_not_let_the_rate_rise()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Memory.Reason = "memory_ceiling";

        for (var i = 0; i < 8; i++)
        {
            rig.Move("u1");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100);
    }

    [Fact]
    public async Task A_schedule_pause_does_not_let_the_rate_climb()
    {
        // Closed from 22:00 to 06:00 is open; the clock starts at noon, so the mover is paused for ten hours.
        var rig = new Rig(o => o.Windows = [new MoveWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) }]);

        for (var i = 0; i < 60; i++)
        {
            rig.Move("u1");
            (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(SchedulePause);
            rig.Time.Advance(TimeSpan.FromMinutes(10));
        }

        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100);
    }

    [Fact]
    public async Task An_OOM_backoff_does_not_let_the_rate_climb()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);
        rig.Rate.Observed("u1", MoveResult.Skip("oom"), TimeSpan.FromMilliseconds(5));

        for (var i = 0; i < 29; i++)
        {
            rig.Move("u1");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(OomPause);
        }

        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100);
    }

    [Fact]
    public async Task A_unit_that_moved_nothing_since_the_last_reading_does_not_climb_and_one_that_moves_does()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);
        for (var i = 0; i < 12; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100, "two minutes of green with no key moved");

        for (var i = 0; i < 6; i++)
        {
            rig.Move("u1");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        rig.Rate.RateOf("u1").KeysPerSecond.Should().BeApproximately(110, 1e-9);
    }

    [Fact]
    public async Task The_rate_stops_at_the_per_unit_maximum_and_never_reaches_infinity()
    {
        var rig = new Rig(o => (o.IncreaseStep, o.IncreaseInterval, o.MaxKeysPerSecond, o.MaxBytesPerSecond) = (10, TimeSpan.FromSeconds(10), 400, 2 * 1024 * 1024));
        await rig.Rate.PauseReasonAsync("u1", Ct);

        for (var i = 0; i < 2000; i++)
        {
            rig.Move("u1");
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        rig.Rate.RateOf("u1").Should().Be((400d, 2d * 1024 * 1024));
        rig.Rate.Reserve("u1", 4000, 0).Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_rate_that_a_reload_put_under_the_current_one_takes_effect_in_the_wait()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);

        rig.Options.MaxKeysPerSecond = 50;

        rig.Rate.Reserve("u1", 100, 0).Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_reload_that_fails_validation_keeps_the_last_good_options()
    {
        var good = new KeyMoveMoverOptions { StartKeysPerSecond = 10, MaxKeysPerSecond = 10 };
        var monitor = new FlakyOptions(good);
        var rate = new AimdMoveRate(new FakeHealth(), null, monitor, new ManualTimeProvider(Noon));
        await rate.PauseReasonAsync("u1", Ct);

        monitor.Invalid = true;

        (await rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
        rate.Reserve("u1", 10, 0).Should().Be(TimeSpan.FromSeconds(1), "the rate of the last good options, not the default of 100 keys per second");
    }

    [Fact]
    public async Task An_unknown_a_default_an_undefined_or_a_missing_verdict_reads_red_and_green_is_the_only_state_that_lets_the_rate_rise()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);

        foreach (var verdict in new KeyMoveHealth?[] { new(KeyMoveHealthState.Unknown, string.Empty), new(default, string.Empty), new((KeyMoveHealthState)7, "bogus"), new((KeyMoveHealthState)(-1), "bogus"), null })
        {
            rig.Health.Next = () => verdict!;
            for (var i = 0; i < 8; i++)
            {
                rig.Move("u1");
                rig.Time.Advance(TimeSpan.FromSeconds(10));
                (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be("red", verdict?.State.ToString() ?? "null");
            }

            rig.Rate.RateOf("u1").KeysPerSecond.Should().Be(100);
        }

        default(KeyMoveHealthState).Should().Be(KeyMoveHealthState.Unknown);
    }

    [Fact]
    public async Task A_source_that_never_answers_reads_red_after_the_timeout_and_one_that_cancels_itself_reads_red_without_stopping_the_unit()
    {
        var rig = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Health.Next = () => throw new InvalidOperationException("unused");
        rig.Health.Async = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, token);
            return new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty);
        };

        var idle = rig.Time.TimerCount;
        var pending = rig.Rate.PauseReasonAsync("u1", Ct).AsTask();
        await entered.Task.WaitAsync(Ct);

        // The timeout's timer is armed after the source is entered: move the clock only once it exists, or the advance lands before it.
        await MoverKit.WaitAsync(() => Task.FromResult(rig.Time.TimerCount > idle));
        pending.IsCompleted.Should().BeFalse();
        rig.Time.Advance(TimeSpan.FromSeconds(6));

        (await pending.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red", "a source that does not answer in time is not green");

        var cancelled = 0;
        rig.Health.Async = _ =>
        {
            Interlocked.Increment(ref cancelled);
            throw new TaskCanceledException("the platform monitor timed out");
        };

        // The timed-out call ends only once its cancellation has run off the gate; until then each evaluation reads red without asking the source, so evaluate until it is asked.
        await MoverKit.WaitAsync(async () =>
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be("red", "the source's own cancellation is a red, not a stop");
            return Volatile.Read(ref cancelled) > 0;
        });

        using var stop = new CancellationTokenSource();
        rig.Health.Async = async token =>
        {
            await stop.CancelAsync();
            token.ThrowIfCancellationRequested();
            return new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty);
        };
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        var act = async () => await rig.Rate.PauseReasonAsync("u1", stop.Token);
        await act.Should().ThrowAsync<OperationCanceledException>("the unit's own cancellation still stops it");
    }

    [Fact]
    public async Task A_source_that_ignores_its_token_reads_red_after_the_timeout_and_is_not_called_again_while_it_runs()
    {
        var rig = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
        var never = new TaskCompletionSource<KeyMoveHealth>();
        rig.Health.Async = _ => never.Task;

        var pending = rig.Rate.PauseReasonAsync("u1", Ct).AsTask();
        await WaitForTimerAsync(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(6));

        (await pending.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red");

        // The hung call is still running: the next evaluation reads red from it and starts no second call, and the one after the call returned takes its own turn.
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        (await rig.Rate.PauseReasonAsync("u1", Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red");
        never.Task.IsCompleted.Should().BeFalse();

        never.SetResult(new KeyMoveHealth(KeyMoveHealthState.Red, string.Empty));
        rig.Health.Async = _ => Task.FromResult(new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty));
        string? reason = "red";
        for (var i = 0; i < 100 && reason is not null; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            reason = await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        reason.Should().BeNull("the first evaluation after the hung call returned makes a call of its own");
    }

    [Fact]
    [SuppressMessage("SonarAnalyzer.CSharp", "S1215", Justification = "Finalization raises the task's unobserved exception event.")]
    public async Task A_timeout_that_fires_as_the_read_ends_never_throws_on_a_pool_thread_and_a_late_failure_is_observed()
    {
        var owned = new HashSet<int>();
        var unobserved = new List<int>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (sender is Task task && owned.Contains(task.Id))
            {
                unobserved.Add(task.Id);
                args.SetObserved();
            }
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            owned.Add(DrainCompletedFailure());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            unobserved.Should().BeEmpty();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        for (var i = 0; i < 200; i++)
        {
            var rig = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
            var answer = new TaskCompletionSource<KeyMoveHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Health.Async = _ => answer.Task;
            var pending = rig.Rate.PauseReasonAsync("u1", Ct).AsTask();
            await WaitForTimerAsync(rig);
            var race = Task.Run(() => answer.TrySetResult(new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty)), Ct);
            rig.Time.Advance(TimeSpan.FromSeconds(6));
            await race;
            var verdict = await pending;
            (verdict is null or "red").Should().BeTrue();
            await rig.Rate.HealthObservation;
            await rig.Rate.HealthCancellation;
        }

        var rigLate = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
        var late = new TaskCompletionSource<KeyMoveHealth>();
        rigLate.Health.Async = _ => late.Task;
        var waiting = rigLate.Rate.PauseReasonAsync("u1", Ct).AsTask();
        await WaitForTimerAsync(rigLate);
        rigLate.Time.Advance(TimeSpan.FromSeconds(6));
        await waiting;
        late.SetException(new InvalidOperationException("late-health-failure"));
        await rigLate.Rate.HealthObservation;
        await rigLate.Rate.HealthCancellation;
    }

    [Fact]
    public async Task A_reload_that_fails_validation_keeps_the_default_stack_running_on_the_last_good_options()
    {
        var good = new KeyMoveMoverOptions { StartKeysPerSecond = 10, MaxKeysPerSecond = 10 };
        var monitor = new FlakyOptions(good);
        var time = new ManualTimeProvider(Noon);
        var reads = 0;
        var rate = AimdMoveRate.Compose(
            _ =>
            {
                reads++;
                return Task.FromResult<IReadOnlyList<NodeInfo>>([new NodeInfo("u1", false, 1024, 0, "allkeys-lru", 0, 1, 100, null)]);
            },
            monitor,
            time);
        (await rate.PauseReasonAsync("u1", Ct)).Should().BeNull();

        monitor.Invalid = true;
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            (await rate.PauseReasonAsync("u1", Ct)).Should().BeNull("a bad reload is not an outage: the health is read on the last good options");
        }

        reads.Should().Be(5, "the watch kept reading INFO on its own interval");
        rate.Reserve("u1", 10, 0).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void At_the_floor_the_longest_wait_is_one_key_and_the_largest_value_and_no_clamp_is_reached()
    {
        var floors = new KeyMoveMoverOptions { StartKeysPerSecond = 0.1, MinKeysPerSecond = 0.1, MaxKeysPerSecond = 0.1, StartBytesPerSecond = 1024, MinBytesPerSecond = 1024, MaxBytesPerSecond = 1024 };
        new KeyMoveMoverOptionsValidator().Validate(null, floors).Succeeded.Should().BeTrue("the floor itself is a valid rate");
        var rig = new Rig(o => (o.StartKeysPerSecond, o.MinKeysPerSecond, o.MaxKeysPerSecond, o.StartBytesPerSecond, o.MinBytesPerSecond, o.MaxBytesPerSecond) = (0.1, 0.1, 0.1, 1024, 1024, 1024));

        rig.Rate.Reserve("key", 1, 0).Should().Be(TimeSpan.FromSeconds(10));
        rig.Rate.Reserve("value", 0, 256 * 1024).Should().Be(TimeSpan.FromSeconds(256), "the largest value the mover moves, at 1 KiB per second");
        rig.Rate.Reserve("batch", 1, 256 * 1024).Should().BeLessThan(TimeSpan.FromMinutes(5), "a key and a value at the floor are the longest wait there is");
    }

    [Fact]
    public void A_wait_in_progress_is_costed_again_at_each_look_so_a_reload_of_the_cap_shortens_or_lengthens_it()
    {
        var rig = new Rig(o => o.GlobalKeysPerSecond = 1);

        rig.Rate.Reserve("u1", 100, 0).Should().Be(TimeSpan.FromSeconds(100));
        rig.Rate.Remaining("u1").Should().Be(TimeSpan.FromSeconds(100));
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Rate.Remaining("u1").Should().Be(TimeSpan.FromSeconds(90));

        rig.Options.GlobalKeysPerSecond = 5;
        rig.Rate.Remaining("u1").Should().Be(TimeSpan.FromSeconds(10), "100 keys at 5 a second end 20 s after the start, 10 s from now");

        rig.Options.GlobalKeysPerSecond = 2;
        rig.Rate.Remaining("u1").Should().Be(TimeSpan.FromSeconds(40), "and a lower cap lengthens it again");

        rig.Options.GlobalKeysPerSecond = 0;
        rig.Rate.Remaining("u1").Should().Be(TimeSpan.Zero, "with no cap only the unit's own rate counts: 100 keys end a second after the start");
        rig.Rate.Remaining("u1").Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_debt_is_kept_on_the_rate_so_the_runner_that_takes_the_unit_pays_it()
    {
        var rig = new Rig();

        rig.Rate.Owe("u1", 4000);
        rig.Rate.Owe("u1", 1000);

        rig.Rate.TakeDebt("u2").Should().Be(0, "a debt is the unit's");
        rig.Rate.TakeDebt("u1").Should().Be(5000, "whichever runner holds the unit takes what the earlier ones left");
        rig.Rate.TakeDebt("u1").Should().Be(0);
    }

    [Fact]
    public void Each_waiter_under_the_global_cap_keeps_its_own_deadline_however_often_the_others_reserve_again()
    {
        var rig = new Rig(o => o.GlobalKeysPerSecond = 100);

        rig.Rate.Reserve("u1", 100, 0).Should().Be(TimeSpan.FromSeconds(1));
        rig.Rate.Reserve("u2", 100, 0).Should().Be(TimeSpan.FromSeconds(2));
        rig.Rate.Reserve("u3", 100, 0).Should().Be(TimeSpan.FromSeconds(3));

        // Each second the waiter whose turn it is wakes and reserves again, which moves the tail of the queue; the others still wake at their own second.
        for (var second = 1; second <= 3; second++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(1));
            var waiter = "u" + second;
            rig.Rate.Remaining(waiter).Should().Be(TimeSpan.Zero, $"{waiter} woke at its own deadline");
            for (var other = second + 1; other <= 3; other++)
            {
                rig.Rate.Remaining("u" + other).Should().Be(TimeSpan.FromSeconds(other - second), $"u{other} still has its own deadline, not the tail of the queue");
            }

            rig.Rate.Reserve(waiter, 100, 0);
        }
    }

    [Fact]
    public async Task Completed_work_whose_cancellation_callback_still_blocks_keeps_its_flight_and_starts_no_new_call_and_nothing_throws()
    {
        using var block = new ManualResetEventSlim();
        try
        {
            var rig = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
            var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new TaskCompletionSource<KeyMoveHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
            rig.Health.Async = token =>
            {
                token.Register(() =>
                {
                    inside.TrySetResult();
                    block.Wait(CancellationToken.None);
                });
                return work.Task;
            };
            var first = rig.Rate.PauseReasonAsync("u1", Ct).AsTask();
            await WaitForTimerAsync(rig);
            await Task.Run(() => rig.Time.Advance(TimeSpan.FromSeconds(6)), Ct).WaitAsync(Ct);
            (await first.WaitAsync(Ct)).Should().Be("red");
            await inside.Task.WaitAsync(Ct);

            // The work returns while the callback of its cancellation is still running.
            work.SetResult(new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty));
            await rig.Rate.HealthObservation;
            for (var i = 0; i < 5; i++)
            {
                rig.Time.Advance(TimeSpan.FromSeconds(10));
                (await rig.Rate.PauseReasonAsync("u1", Ct).AsTask().WaitAsync(Ct)).Should().Be("red", "the flight is held until its cancellation has ended");
            }

            rig.Health.Reads.Should().Be(1, "no second call, and so no second blocked cancellation, starts meanwhile");

            block.Set();
            await rig.Rate.HealthCancellation;
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
            rig.Health.Reads.Should().Be(2);
            await rig.Rate.HealthObservation;
        }
        finally
        {
            block.Set();
        }
    }

    [Fact]
    public async Task A_source_that_never_answers_has_one_call_outstanding_across_many_evaluations_and_reads_red_each_time()
    {
        var rig = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
        var never = new TaskCompletionSource<KeyMoveHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Health.Async = _ => never.Task;
        var first = rig.Rate.PauseReasonAsync("u1", Ct).AsTask();
        await WaitForTimerAsync(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        (await first.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red");

        for (var i = 0; i < 100; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            (await rig.Rate.PauseReasonAsync("u1", Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red");
        }

        rig.Health.Reads.Should().Be(1, "the call that is still running is not replaced by a second one");
        rig.Time.TimerCount.Should().Be(0, "nothing is left waiting for the hung source");

        never.SetResult(new KeyMoveHealth(KeyMoveHealthState.Green, string.Empty));
        rig.Health.Async = null;

        // The call finishes on a pool thread; from the evaluation after it, a new call is made.
        string? reason = "red";
        for (var i = 0; i < 100 && reason is not null; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, Ct);
            rig.Time.Advance(TimeSpan.FromSeconds(10));
            reason = await rig.Rate.PauseReasonAsync("u1", Ct);
        }

        reason.Should().BeNull("a call that has returned is followed by a new one");
        rig.Health.Reads.Should().Be(2);
    }

    [Fact]
    public async Task A_cancellation_callback_that_blocks_does_not_hold_the_refresh_gate_and_the_next_evaluation_goes_on()
    {
        var rig = new Rig(o => o.HealthTimeout = TimeSpan.FromSeconds(5));
        using var block = new ManualResetEventSlim();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<KeyMoveHealth>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Health.Async = token =>
        {
            token.Register(() =>
            {
                inside.TrySetResult();
                block.Wait(CancellationToken.None);
            });
            return never.Task;
        };
        try
        {
            var first = rig.Rate.PauseReasonAsync("u1", Ct).AsTask();
            await WaitForTimerAsync(rig);

            // The timer fires on the thread that advances the clock, so a time limit that waited for the callbacks would hold this thread: advance on another.
            await Task.Run(() => rig.Time.Advance(TimeSpan.FromSeconds(6)), Ct).WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);

            (await first.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red", "the time limit does not wait for the callbacks of the cancellation");
            await inside.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct);

            rig.Time.Advance(TimeSpan.FromSeconds(10));
            (await rig.Rate.PauseReasonAsync("u1", Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, Ct)).Should().Be("red", "the gate is free while the callback still blocks");
        }
        finally
        {
            block.Set();
        }
    }

    [Fact]
    public async Task One_rate_serves_two_hosts_and_the_cap_holds_across_both()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScanUnits>(new NoUnits());
        services.AddSingleton<TimeProvider>(new ManualTimeProvider(Noon));
        services.Configure<KeyMoveMoverOptions>(o => o.GlobalKeysPerSecond = 100);
        services.AddMoverRate();
        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IMoveRate>();
        var second = provider.GetRequiredService<IMoveRate>();

        second.Should().BeSameAs(first).And.BeSameAs(provider.GetRequiredService<AimdMoveRate>());
        first.Reserve("group-a/u1", 100, 0).Should().Be(TimeSpan.FromSeconds(1));
        second.Reserve("group-b/u1", 100, 0).Should().Be(TimeSpan.FromSeconds(2), "the second host's batch queues behind the first's under one cap of the node");
    }

    [Fact]
    public async Task An_OOM_reply_stops_every_unit_for_minutes_and_then_the_mover_resumes()
    {
        var rig = new Rig();
        await rig.Rate.PauseReasonAsync("u1", Ct);

        rig.Rate.Observed("u1", MoveResult.Skip("oom"), TimeSpan.FromMilliseconds(5));

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(OomPause);
        (await rig.Rate.PauseReasonAsync("u2", Ct)).Should().Be(OomPause);
        rig.Time.Advance(TimeSpan.FromMinutes(4));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(OomPause);
        rig.Time.Advance(TimeSpan.FromMinutes(1.01));
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task The_schedule_outranks_a_memory_pause_and_the_memory_pause_outranks_red()
    {
        var rig = new Rig(o => o.Windows = [new MoveWindow { Start = TimeSpan.FromHours(22), End = TimeSpan.FromHours(6) }]);
        rig.Health.State = KeyMoveHealthState.Red;
        rig.Memory.Reason = EvictionPause;

        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(SchedulePause);

        rig.Time.Advance(TimeSpan.FromHours(11));
        rig.Memory.Reason = EvictionPause;
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().Be(EvictionPause);
    }

    [Fact]
    public async Task What_the_mover_observes_feeds_its_own_p99_and_timeouts_and_nothing_else()
    {
        var rig = new Rig();
        var latency = rig.Latency;
        for (var i = 0; i < 30; i++)
        {
            rig.Rate.Observed("u1", MoveResult.Done, TimeSpan.FromMilliseconds(10));
        }

        rig.Rate.Observed("u1", MoveResult.Skip("timeout"), TimeSpan.FromSeconds(5));

        latency.P99(TimeSpan.FromMinutes(1)).Should().Be(TimeSpan.FromSeconds(5));
        latency.TakeTimeouts().Should().Be(1);
        latency.TakeTimeouts().Should().Be(0);
        (await rig.Rate.PauseReasonAsync("u1", Ct)).Should().BeNull("a timeout is a signal for the health source, not a pause of its own");
    }

    private static async Task WaitForTimerAsync(Rig rig)
    {
        while (!rig.Time.HasTimers)
        {
            await rig.Time.NextTimerAsync(Ct);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int DrainCompletedFailure()
    {
        var core = new RouterCore(new ManualTimeProvider(Noon), _ => true, NullLogger.Instance);
        var task = Task.FromException(new InvalidOperationException("owned-drain-failure"));
        var id = task.Id;
        core.AddWithoutRemoval(task);
        core.DrainAsync().IsCompletedSuccessfully.Should().BeTrue();
        return id;
    }

    internal sealed class Rig
    {
        public Rig(Action<KeyMoveMoverOptions>? configure = null)
        {
            Time = new ManualTimeProvider(Noon);
            Options = new KeyMoveMoverOptions();
            configure?.Invoke(Options);
            Latency = new MoverLatency(Time);
            Rate = new AimdMoveRate(Health, Memory, new StaticOptionsMonitor<KeyMoveMoverOptions>(Options), Time, Latency);
        }

        public ManualTimeProvider Time { get; }

        public KeyMoveMoverOptions Options { get; }

        public FakeHealth Health { get; } = new();

        public FakeMemory Memory { get; } = new();

        public MoverLatency Latency { get; }

        public AimdMoveRate Rate { get; }

        // The unit moved a key since the last reading.
        public void Move(string unit) => Rate.Observed(unit, MoveResult.Done, TimeSpan.FromMilliseconds(1));
    }

    internal sealed class NoUnits : IScanUnits
    {
        public Task<IReadOnlyList<ScanUnit>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ScanUnit>>([]);
    }

    internal sealed class FakeHealth : IKeyMoveHealthSource
    {
        public KeyMoveHealthState State { get; set; } = KeyMoveHealthState.Green;

        public bool Throw { get; set; }

        public Func<KeyMoveHealth>? Next { get; set; }

        public Func<CancellationToken, Task<KeyMoveHealth>>? Async { get; set; }

        public int Reads { get; private set; }

        public async ValueTask<KeyMoveHealth> GetHealthAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (Async is { } readAsync)
            {
                return await readAsync(cancellationToken);
            }

            if (Throw)
            {
                throw new InvalidOperationException("The platform monitor is down.");
            }

            return Next is { } next ? next() : new KeyMoveHealth(State, State.ToString());
        }
    }

    internal sealed class FakeMemory : IMoverMemory
    {
        public string? Reason { get; set; }

        public bool Throw { get; set; }

        public ValueTask<string?> PauseReasonAsync(CancellationToken cancellationToken) =>
            Throw ? throw new InvalidOperationException("INFO refused.") : ValueTask.FromResult(Reason);
    }

    // An options monitor whose reading throws as a reload that fails validation does.
    private sealed class FlakyOptions(KeyMoveMoverOptions good) : IOptionsMonitor<KeyMoveMoverOptions>
    {
        public bool Invalid { get; set; }

        public KeyMoveMoverOptions CurrentValue => Invalid ? throw new OptionsValidationException(string.Empty, typeof(KeyMoveMoverOptions), ["bad reload"]) : good;

        public KeyMoveMoverOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<KeyMoveMoverOptions, string?> listener) => null;
    }
}
