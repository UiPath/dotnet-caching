using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests;

public class BackgroundWritesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Before_the_first_background_write_nothing_is_tracked_and_a_read_spanning_it_is_not_kept()
    {
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
        var mark = writes.Mark("k");

        using (var turn = writes.Enter("k"))
        {
            turn.Previous.IsCompleted.Should().BeTrue();
        }

        writes.Active.Should().BeFalse();
        writes.Fill("k", mark, 0, static _ => true).Should().BeTrue();
        writes.TryReserve().Should().BeTrue();
        writes.Active.Should().BeTrue();
        writes.Fill("k", mark, 0, static _ => true).Should().BeNull("the read began before the order was tracked");
        writes.Release();
    }

    [Fact]
    public void A_fill_tells_a_change_since_its_mark_from_a_local_write_that_refused()
    {
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System);
        writes.TryReserve().Should().BeTrue();
        writes.Release();
        var mark = writes.Mark("k");

        writes.Fill("k", mark, 0, static _ => false).Should().BeFalse();
        writes.Apply("k", 0, static _ => true);
        writes.Fill("k", mark, 0, static _ => true).Should().BeNull();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task A_background_write_whose_local_write_a_later_one_of_the_key_overtook_leaves_the_later_value()
    {
        var local = new List<string>();
        using var firstIssued = new ManualResetEventSlim();
        using var secondDone = new ManualResetEventSlim();
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System, onIssued: _ =>
        {
            if (!firstIssued.IsSet)
            {
                firstIssued.Set();
                secondDone.Wait(TimeSpan.FromSeconds(5));
            }
        });
        writes.TryReserve().Should().BeTrue();
        writes.TryReserve().Should().BeTrue();
        bool Record(string value)
        {
            lock (local)
            {
                local.Add(value);
            }

            return true;
        }

        var first = Task.Run(() => writes.Run("k", "v1", Record, static (_, _) => ValueTask.CompletedTask), TestContext.Current.CancellationToken);
        firstIssued.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Should().BeTrue();
        writes.Run("k", "v2", Record, static (_, _) => ValueTask.CompletedTask);
        secondDone.Set();

        (await first).Should().BeTrue("the newer value is the one kept");
        local.Should().Equal("v2");
        await writes.DrainAsync();
    }

    [Fact]
    public async Task A_write_waits_for_the_background_write_issued_before_it_to_settle()
    {
        var writes = Create();
        var order = new List<string>();
        var background = Gate();
        Reserve(writes, "k", async _ =>
        {
            order.Add("background");
            await background.Task;
        });

        var write = writes.WriteAsync("k", order, null, static (o, _) => Wrote(o, "write"), static (_, written, _) => written, Ct);

        write.IsCompleted.Should().BeFalse();
        order.Should().Equal("background");

        background.SetResult();
        (await write.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        order.Should().Equal("background", "write");
        await writes.DrainAsync();
    }

    [Fact]
    public async Task Queued_background_writes_of_a_key_are_replaced_by_the_newest_and_give_their_places_back()
    {
        var writes = Create(4);
        var ran = new List<string>();
        var gate = Gate();
        foreach (var value in new[] { "v1", "v2", "v3", "v4" })
        {
            Reserve(writes, "k", async _ =>
            {
                ran.Add(value);
                await gate.Task;
            });
        }

        writes.InFlight.Should().Be(2);
        writes.TryReserve().Should().BeTrue();
        writes.Release();

        gate.SetResult();
        await writes.DrainAsync();
        ran.Should().Equal("v1", "v4");
    }

    [Fact]
    public async Task A_replacing_write_past_the_limit_gives_its_place_back_and_is_counted()
    {
        var telemetry = Substitute.For<ICachingTelemetryProvider>();
        var writes = new BackgroundWrites(2, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, telemetry, TimeProvider.System);
        var gate = Gate();
        Reserve(writes, "k", async _ => await gate.Task);
        Reserve(writes, "k", _ => ValueTask.CompletedTask);
        writes.InFlight.Should().Be(2);

        writes.TryReserve().Should().BeFalse();
        writes.TryReserve("k", true).Should().BeTrue("a queued write of the key takes the new one in place");
        writes.InFlight.Should().Be(3);
        writes.Run("k", 0, static _ => true, static (_, _) => ValueTask.CompletedTask);

        writes.InFlight.Should().Be(2);
        telemetry.Received(1).TrackMetric("cache.background_write.coalesced", 1, Arg.Any<TelemetryTags<string>>());
        gate.SetResult();
        await writes.DrainAsync();
        writes.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task A_place_reserved_past_the_limit_goes_back_when_the_queued_write_it_was_to_replace_started_first()
    {
        var writes = Create(2);
        var ran = new List<string>();
        var other = Gate();
        var awaited = Gate();
        var queued = Gate();
        var started = Gate();
        Reserve(writes, "other", async _ => await other.Task);
        // Queued behind an awaited write of the key, which holds no place: when it ends, the queued write starts without giving a place back.
        async ValueTask<bool> AwaitedWrite(List<string> r, BackgroundWrites.Keeper _)
        {
            await awaited.Task;
            return Record(r, "awaited");
        }

        var write = writes.WriteAsync("k", ran, null, AwaitedWrite, static (_, written, _) => written, Ct);
        Reserve(writes, "k", async _ =>
        {
            ran.Add("queued");
            started.SetResult();
            await queued.Task;
        });
        writes.InFlight.Should().Be(2);
        writes.TryReserve("k", true).Should().BeTrue("the queued write can take the new one in place");
        writes.InFlight.Should().Be(3);

        awaited.SetResult();
        (await write.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        await started.Task.WaitAsync(Wait, Ct);

        var kept = writes.Run("k", 0, static _ => true, (_, _) => Ran(ran, "new"));
        writes.InFlight.Should().Be(2, "a write past the limit that can no longer replace the queued one takes no place");
        kept.Should().BeNull();

        // As the cache does after a refused reservation: in the foreground, after the queued write.
        var foreground = writes.WriteAsync("k", ran, null, static (r, _) => Wrote(r, "new"), static (_, written, _) => written, Ct);
        queued.SetResult();
        other.SetResult();
        (await foreground.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        await writes.DrainAsync();
        ran.Should().Equal("awaited", "queued", "new");
        writes.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task A_write_without_an_event_does_not_take_the_place_of_a_queued_one_that_sends_it()
    {
        var writes = Create(3);
        var ran = new List<string>();
        var gate = Gate();
        Reserve(writes, "k", async _ =>
        {
            ran.Add("v1");
            await gate.Task;
        });
        Reserve(writes, "k", _ => Ran(ran, "set"));

        writes.TryReserve("k", false).Should().BeTrue();
        writes.InFlight.Should().Be(3);
        writes.RunWithoutEvent("k", 0, static _ => true, (_, _) => Ran(ran, "fill"));
        writes.InFlight.Should().Be(3, "the fill queued behind the set instead of replacing it");
        gate.SetResult();
        await writes.DrainAsync();
        ran.Should().Equal("v1", "set", "fill");
    }

    [Fact]
    public async Task A_write_that_sends_the_event_takes_the_place_of_a_queued_one_that_does_not()
    {
        var writes = Create(3);
        var ran = new List<string>();
        var gate = Gate();
        Reserve(writes, "k", async _ =>
        {
            ran.Add("v1");
            await gate.Task;
        });
        writes.TryReserve("k", false).Should().BeTrue();
        writes.RunWithoutEvent("k", 0, static _ => true, (_, _) => Ran(ran, "fill"));
        writes.TryReserve("k", true).Should().BeTrue();
        writes.Run("k", 0, static _ => true, (_, _) => Ran(ran, "set"));

        writes.InFlight.Should().Be(2);
        gate.SetResult();
        await writes.DrainAsync();
        ran.Should().Equal("v1", "set");
    }

    [Fact]
    public async Task A_replaced_write_does_not_start_before_the_write_that_replaced_it_has_made_its_local_write()
    {
        var gate = Gate();
        var started = Gate();
        var issued = 0;
        var localDone = false;
        bool? sawLocal = null;
        var writes = new BackgroundWrites(4, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), TimeProvider.System, onIssued: _ =>
        {
            if (++issued == 3)
            {
                // Between the swap and the local write: the job must not start here.
                gate.SetResult();
                started.Task.Wait(TimeSpan.FromMilliseconds(250), Ct);
            }
        });
        Reserve(writes, "k", async _ => await gate.Task);
        Reserve(writes, "k", _ => throw new InvalidOperationException("replaced"));
        writes.TryReserve("k", true).Should().BeTrue();
        bool Local(int _)
        {
            localDone = true;
            return true;
        }

        ValueTask Work(int _, CancellationToken __)
        {
            sawLocal = Volatile.Read(ref localDone);
            started.TrySetResult();
            return ValueTask.CompletedTask;
        }

        writes.Run("k", 0, Local, Work);

        await writes.DrainAsync();
        sawLocal.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(30)]
    public async Task Many_writes_of_one_key_land_the_last_one_everywhere_and_every_awaited_one_completes(int awaitedPercent)
    {
        for (var round = 0; round < 20; round++)
        {
            var rng = new Random(round);
            var writes = Create(4);
            var inner = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var local = 0L;
            var awaited = new List<Task<bool>>();
            const int Count = 200;
            for (var i = 1; i <= Count; i++)
            {
                if (i == Count)
                {
                    await Task.WhenAll(awaited).WaitAsync(Wait, Ct);
                }

                if ((i == Count || rng.Next(100) >= awaitedPercent) && writes.TryReserve("k", true))
                {
                    bool Keep(int s)
                    {
                        RaiseTo(ref local, s);
                        return true;
                    }

                    async ValueTask Work(int s, CancellationToken _)
                    {
                        await Task.Yield();
                        inner.Enqueue(s);
                    }

                    writes.Run("k", i, Keep, Work);
                }
                else
                {
                    async ValueTask<bool> Write(int s, BackgroundWrites.Keeper _)
                    {
                        await Task.Yield();
                        inner.Enqueue(s);
                        return true;
                    }

                    bool Keep(int s, bool written, BackgroundWrites.Keeper keeper) => keeper.Local("k", s, x =>
                    {
                        RaiseTo(ref local, x);
                        return written;
                    });

                    awaited.Add(writes.WriteAsync("k", i, null, Write, Keep, Ct).AsTask());
                }

                if (rng.Next(4) == 0)
                {
                    await Task.Yield();
                }
            }

            (await Task.WhenAll(awaited).WaitAsync(Wait, Ct)).Should().OnlyContain(w => w);
            await writes.DrainAsync().WaitAsync(Wait, Ct);
            inner.Last().Should().Be(Count);
            Volatile.Read(ref local).Should().Be(Count);
            writes.InFlight.Should().Be(0);
        }
    }

    [Fact]
    public async Task An_awaited_write_between_background_writes_keeps_its_place()
    {
        var writes = Create();
        var ran = new List<string>();
        var gate = Gate();
        Reserve(writes, "k", async _ =>
        {
            ran.Add("v1");
            await gate.Task;
        });
        var awaited = writes.WriteAsync("k", ran, null, static (o, _) => Wrote(o, "v2"), static (_, written, _) => written, Ct).AsTask();
        Reserve(writes, "k", _ => Ran(ran, "v3"));
        Reserve(writes, "k", _ => Ran(ran, "v4"));

        awaited.IsCompleted.Should().BeFalse();
        gate.SetResult();
        (await awaited.WaitAsync(Wait, Ct)).Should().BeTrue();
        await writes.DrainAsync();
        ran.Should().Equal("v1", "v2", "v4");
    }

    [Fact]
    public async Task A_write_of_several_keys_waits_for_the_background_write_of_any_of_them()
    {
        var writes = Create();
        var order = new List<string>();
        var background = Gate();
        Reserve(writes, "b", async _ => await background.Task);

        var write = writes.WriteAsync(["a", "b"], order, null, static o => Wrote(o, "write"), Ct);

        write.IsCompleted.Should().BeFalse();
        order.Should().BeEmpty();

        background.SetResult();
        (await write.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        order.Should().Equal("write");
        await writes.DrainAsync();
    }

    [Fact]
    public async Task The_before_step_runs_at_once_in_the_turn_while_the_write_waits_for_the_earlier_one()
    {
        var writes = Create();
        var order = new List<string>();
        var background = Gate();
        Reserve(writes, "k", async _ => await background.Task);

        var write = writes.WriteAsync("k", order, static o => o.Add("before"), static o => Wrote(o, "write"), Ct);

        order.Should().Equal("before");

        background.SetResult();
        await write.AsTask().WaitAsync(Wait, Ct);
        order.Should().Equal("before", "write");
        await writes.DrainAsync();
    }

    [Fact]
    public async Task A_write_does_not_wait_for_the_background_write_of_another_key()
    {
        var writes = Create();
        var background = Gate();
        Reserve(writes, "other", async _ => await background.Task);

        var write = writes.WriteAsync("k", 0, null, static (_, _) => ValueTask.FromResult(true), static (_, written, _) => written, Ct);

        (await write.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        background.SetResult();
        await writes.DrainAsync();
    }

    [Fact]
    public async Task A_write_superseded_by_a_background_write_issued_after_it_does_not_keep_its_value_locally()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var inner = Gate();
        var first = writes.WriteAsync(
            "k",
            log,
            null,
            async (_, _) =>
            {
                await inner.Task;
                return true;
            },
            static (l, _, keeper) => keeper.Local("k", l, static x => Record(x, "set"), static x => Record(x, "superseded")),
            Ct);

        Reserve(writes, "k", _ => ValueTask.CompletedTask);
        inner.SetResult();

        (await first.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        log.Should().Equal("superseded");
        await writes.DrainAsync();
    }

    [Fact]
    public async Task A_superseded_write_without_a_superseded_step_is_counted_as_kept_and_sets_nothing()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var inner = Gate();
        var first = writes.WriteAsync(
            "k",
            log,
            null,
            async (_, _) =>
            {
                await inner.Task;
                return true;
            },
            static (l, _, keeper) => keeper.Local("k", l, static x => Record(x, "set")),
            Ct);

        Reserve(writes, "k", _ => ValueTask.CompletedTask);
        inner.SetResult();

        (await first.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
        log.Should().BeEmpty();
        await writes.DrainAsync();
    }

    [Fact]
    public async Task A_write_keeps_locally_when_no_background_write_came_after_it()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();

        var kept = await writes.WriteAsync("k", log, null, static (_, _) => ValueTask.FromResult(true), static (l, _, keeper) => keeper.Local("k", l, static x => Record(x, "set")), Ct);

        kept.Should().BeTrue();
        log.Should().Equal("set");
    }

    [Fact]
    public async Task A_write_that_returned_a_lower_version_than_one_that_returned_before_it_runs_the_superseded_step_instead()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var older = Gate();
        var newer = Gate();
        var first = WriteVersioned(writes, log, "first", older, 3);
        var second = WriteVersioned(writes, log, "second", newer, 5);

        newer.SetResult();
        await second.WaitAsync(Wait, Ct);
        older.SetResult();
        await first.WaitAsync(Wait, Ct);

        log.Should().Equal("second set", "first superseded");
    }

    [Fact]
    public async Task A_write_that_returned_a_higher_version_than_the_ones_before_it_keeps_its_value()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var older = Gate();
        var newer = Gate();
        var first = WriteVersioned(writes, log, "first", older, 3);
        var second = WriteVersioned(writes, log, "second", newer, 5);

        older.SetResult();
        await first.WaitAsync(Wait, Ct);
        newer.SetResult();
        await second.WaitAsync(Wait, Ct);

        log.Should().Equal("first set", "second set");
    }

    [Fact]
    public async Task Counting_alone_refuses_a_fill_begun_before_an_awaited_write_kept_and_orders_no_write()
    {
        var writes = Create();
        writes.Track();
        var mark = writes.Mark("k");
        var other = writes.Mark("n");

        (await writes.WriteAsync("k", 0, null, static (_, _) => ValueTask.FromResult(true), static (_, _, keeper) => keeper.Local("k", 0, static _ => true), Ct)).Should().BeTrue();

        writes.Active.Should().BeFalse();
        writes.Fill("k", mark, 0, static _ => true).Should().BeNull();
        writes.Fill("n", other, 0, static _ => true).Should().Be(BackgroundWrites.Stripe("n") != BackgroundWrites.Stripe("k") ? true : null);
    }

    [Fact]
    public void A_refused_fill_is_counted_by_why_it_was_refused()
    {
        var telemetry = new Telemetry.RecordingTelemetryProvider();
        var writes = new BackgroundWrites(8, TimeSpan.FromSeconds(5), "c", NullLogger.Instance, telemetry, TimeProvider.System);
        writes.Track();
        var mark = writes.Mark("k");
        writes.Touch("k");

        writes.Fill("k", mark, 0, static _ => true).Should().BeNull();
        using var turn = writes.Enter(["k"]);
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeNull();

        var refused = telemetry.Metrics.Where(m => m.Name == "cache.background_write.fill_refused").ToArray();
        refused.Select(m => m.Properties!["reason"]).Should().Equal("changed", "in_flight");
        refused.Should().OnlyContain(m => m.Value == 1 && m.Properties!["cache.name"] == "c");
    }

    [Fact]
    public void Counting_alone_refuses_a_fill_while_an_unordered_write_of_its_stripe_is_in_flight_and_after_it_ends()
    {
        var writes = Create();
        writes.Track();
        var before = writes.Mark("k");

        var turn = writes.Enter(["k"]);
        var during = writes.Mark("k");
        writes.Fill("k", during, 0, static _ => true).Should().BeNull("the inner tier may change under the read until the write ends");
        turn.Dispose();

        writes.Active.Should().BeFalse();
        writes.Fill("k", before, 0, static _ => true).Should().BeNull();
        writes.Fill("k", during, 0, static _ => true).Should().BeNull();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public void A_background_write_after_counting_keeps_the_counts()
    {
        var writes = Create();
        writes.Track();
        var mark = writes.Mark("n");

        writes.TryReserve().Should().BeTrue();
        writes.Release();

        writes.Active.Should().BeTrue();
        writes.Fill("n", mark, 0, static _ => true).Should().BeTrue("the counts did not restart");
    }

    [Fact]
    public async Task Before_the_first_background_write_a_write_keeps_at_once_and_does_not_wait()
    {
        var writes = Create();
        var log = new List<string>();

        var kept = await writes.WriteAsync(
            "k",
            log,
            null,
            static (_, keeper) =>
            {
                keeper.Returned("k", 7);
                return ValueTask.FromResult(true);
            },
            static (l, _, keeper) => keeper.Local("k", l, static x => Record(x, "set"), static x => Record(x, "superseded"), 7),
            Ct);

        kept.Should().BeTrue();
        log.Should().Equal("set");
        writes.Active.Should().BeFalse();
    }

    [Fact]
    public async Task A_keep_that_refuses_is_reported_by_the_write()
    {
        var writes = Create();

        var kept = await writes.WriteAsync("k", 0, null, static (_, _) => ValueTask.FromResult(true), static (_, _, keeper) => keeper.Local("k", 0, static _ => false), Ct);

        kept.Should().BeFalse();
    }

    [Fact]
    public async Task The_cap_refuses_a_place_once_the_limit_is_in_flight_and_gives_it_back_when_the_writes_end()
    {
        var writes = Create(limit: 2);
        var gate = Gate();

        Reserve(writes, "a", async _ => await gate.Task);
        Reserve(writes, "b", async _ => await gate.Task);
        writes.TryReserve().Should().BeFalse();
        writes.InFlight.Should().Be(2);

        gate.SetResult();
        await writes.DrainAsync();
        writes.InFlight.Should().Be(0);
    }

    [Fact]
    public void A_released_place_can_be_reserved_again()
    {
        var writes = Create(limit: 1);

        writes.TryReserve().Should().BeTrue();
        writes.TryReserve().Should().BeFalse();
        writes.Release();
        writes.TryReserve().Should().BeTrue();
        writes.Release();
    }

    [Fact]
    public async Task A_drain_waits_for_the_writes_in_flight_and_cancels_nothing_when_they_finish_in_time()
    {
        var clock = new ManualClock();
        var writes = Create(clock: clock);
        var gate = Gate();
        var seen = CancellationToken.None;
        Reserve(writes, "k", async token =>
        {
            seen = token;
            await gate.Task;
        });

        var drain = writes.DrainAsync();
        drain.IsCompleted.Should().BeFalse();
        writes.TryReserve().Should().BeFalse("a closing cache takes no more background writes");

        gate.SetResult();
        await drain.WaitAsync(Wait, Ct);
        seen.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task A_drain_cancels_a_write_still_running_once_the_drain_timeout_has_passed()
    {
        var clock = new ManualClock();
        var writes = Create(drainTimeout: TimeSpan.FromSeconds(30), clock: clock);
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        Reserve(writes, "k", async token =>
        {
            seen.SetResult(token);
            await Gate().Task.WaitAsync(token);
        });

        var drain = writes.DrainAsync();
        clock.Due.Should().Equal(TimeSpan.FromSeconds(30));
        drain.IsCompleted.Should().BeFalse();
        (await seen.Task).IsCancellationRequested.Should().BeFalse();

        clock.Elapse();
        await drain.WaitAsync(Wait, Ct);

        (await seen.Task).IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_drain_that_gives_up_frees_a_write_waiting_behind_a_stuck_awaited_one()
    {
        var clock = new ManualClock();
        var writes = Create(drainTimeout: TimeSpan.FromSeconds(30), clock: clock);
        writes.Activate();
        var stuck = Gate();
        var awaited = writes.WriteAsync(
            "k",
            0,
            null,
            async (_, _) =>
            {
                await stuck.Task;
                return true;
            },
            static (_, written, _) => written,
            Ct);
        var behind = false;
        Reserve(writes, "k", _ =>
        {
            behind = true;
            return ValueTask.CompletedTask;
        });

        var drain = writes.DrainAsync();
        clock.Elapse();
        await drain.WaitAsync(Wait, Ct);

        behind.Should().BeFalse("it was cancelled while it waited");
        stuck.SetResult();
        (await awaited.AsTask().WaitAsync(Wait, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_inner_write_propagates_keeps_nothing_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();
        var kept = false;

        var failing = writes.WriteAsync(
            "k",
            0,
            null,
            static (_, _) => ValueTask.FromException<bool>(new InvalidOperationException("inner")),
            (_, _, _) => kept = true,
            Ct);

        await FluentActions.Awaiting(() => failing.AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("inner");
        kept.Should().BeFalse();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue("the key's turn ended, so a fill is not held back");
    }

    [Fact]
    public async Task A_failure_of_the_inner_write_after_it_awaited_propagates_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();
        var gate = Gate();
        var kept = false;

        var failing = writes.WriteAsync(
            "k",
            0,
            null,
            async ValueTask<bool> (_, _) =>
            {
                await gate.Task;
                throw new InvalidOperationException("late");
            },
            (_, _, _) => kept = true,
            Ct);
        gate.SetResult();

        await FluentActions.Awaiting(() => failing.AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("late");
        kept.Should().BeFalse();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task With_nothing_pending_a_write_that_completes_at_once_completes_synchronously_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();

        var write = writes.WriteAsync("k", 0, null, static (_, _) => ValueTask.FromResult(true), static (_, written, _) => written, Ct);

        write.IsCompletedSuccessfully.Should().BeTrue();
        (await write).Should().BeTrue();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue("the turn ended with the write");
    }

    [Fact]
    public async Task A_write_that_throws_before_returning_comes_back_faulted_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();

        var write = writes.WriteAsync<int, bool, bool>("k", 0, null, static (_, _) => throw new InvalidOperationException("sync"), static (_, written, _) => written, Ct);

        write.IsFaulted.Should().BeTrue("the exception surfaces when the caller awaits, as from an async method");
        await FluentActions.Awaiting(() => write.AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("sync");
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task A_cancellation_thrown_before_the_write_returns_comes_back_cancelled_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();

        var write = writes.WriteAsync<int, bool>("k", 0, static _ => throw new OperationCanceledException(), static _ => ValueTask.FromResult(true), Ct);

        write.IsCanceled.Should().BeTrue();
        await FluentActions.Awaiting(() => write.AsTask()).Should().ThrowAsync<OperationCanceledException>();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_keep_propagates_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();

        var failing = writes.WriteAsync<int, bool, bool>("k", 0, null, static (_, _) => ValueTask.FromResult(true), static (_, _, _) => throw new InvalidOperationException("keep"), Ct);

        await FluentActions.Awaiting(() => failing.AsTask()).Should().ThrowAsync<InvalidOperationException>().WithMessage("keep");
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public async Task A_write_cancelled_while_it_waits_for_an_earlier_one_runs_nothing_and_leaves_the_order()
    {
        var writes = Create();
        using var cts = new CancellationTokenSource();
        var background = Gate();
        Reserve(writes, "k", async _ => await background.Task);
        var order = new List<string>();

        var write = writes.WriteAsync("k", order, null, static o => Wrote(o, "write"), cts.Token);
        write.IsCompleted.Should().BeFalse();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => write.AsTask()).Should().ThrowAsync<OperationCanceledException>();
        order.Should().BeEmpty();

        background.SetResult();
        await writes.DrainAsync();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue("the cancelled write left the key's order");
    }

    [Fact]
    public async Task A_cancelled_inner_write_propagates_its_cancellation_and_frees_the_key()
    {
        var writes = Create();
        writes.Activate();
        using var cts = new CancellationTokenSource();
        var inner = Gate();

        var write = writes.WriteAsync(
            "k",
            0,
            null,
            async (_, _) =>
            {
                await inner.Task.WaitAsync(cts.Token);
                return true;
            },
            static (_, written, _) => written,
            cts.Token);
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => write.AsTask()).Should().ThrowAsync<OperationCanceledException>();
        writes.Fill("k", writes.Mark("k"), 0, static _ => true).Should().BeTrue();
    }

    [Fact]
    public void A_fill_keeps_the_value_when_nothing_changed_since_the_mark()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var mark = writes.Mark("k");

        writes.Fill("k", mark, log, static l => Record(l, "set"), static l => l.Add("not kept"));

        log.Should().Equal("set");
    }

    [Fact]
    public async Task A_fill_tells_when_a_write_of_the_key_came_after_its_read_began()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var mark = writes.Mark("k");
        await writes.WriteAsync("k", 0, null, static (_, _) => ValueTask.FromResult(true), static (_, written, _) => written, Ct);

        writes.Fill("k", mark, log, static l => Record(l, "set"), static l => l.Add("not kept"));

        log.Should().Equal("not kept");
    }

    [Fact]
    public void A_fill_tells_when_a_write_of_the_key_is_in_flight()
    {
        var writes = Create();
        writes.Activate();
        var log = new List<string>();
        var gate = Gate();
        Reserve(writes, "k", async _ => await gate.Task);
        var mark = writes.Mark("k");

        writes.Fill("k", mark, log, static l => Record(l, "set"), static l => l.Add("not kept"));

        log.Should().Equal("not kept");
        gate.SetResult();
    }

    [Fact]
    public void A_fill_keeps_the_value_when_writes_are_not_tracked()
    {
        var writes = Create();
        var log = new List<string>();

        writes.Fill("k", writes.Mark("k"), log, static l => Record(l, "set"), static l => l.Add("not kept"));

        log.Should().Equal("set");
    }

    [Fact]
    public void A_fill_whose_read_began_before_writes_were_tracked_is_not_kept()
    {
        var writes = Create();
        var log = new List<string>();
        var mark = writes.Mark("k");
        writes.Activate();

        writes.Fill("k", mark, log, static l => Record(l, "set"), static l => l.Add("not kept"));

        log.Should().Equal("not kept");
    }

    private static BackgroundWrites Create(int limit = 8, TimeSpan? drainTimeout = null, TimeProvider? clock = null) =>
        new(limit, drainTimeout ?? TimeSpan.FromSeconds(5), "c", NullLogger.Instance, Substitute.For<ICachingTelemetryProvider>(), clock ?? TimeProvider.System);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool Record(List<string> log, string entry)
    {
        log.Add(entry);
        return true;
    }

    private static void RaiseTo(ref long target, long value)
    {
        long seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static ValueTask Ran(List<string> log, string entry)
    {
        log.Add(entry);
        return ValueTask.CompletedTask;
    }

    private static ValueTask<bool> Wrote(List<string> log, string entry) => ValueTask.FromResult(Record(log, entry));

    private static void Reserve(BackgroundWrites writes, string key, Func<CancellationToken, ValueTask> work)
    {
        writes.TryReserve().Should().BeTrue();
        writes.Run(key, work, static _ => true, static (w, token) => w(token));
    }

    private static Task<bool> WriteVersioned(BackgroundWrites writes, List<string> log, string name, TaskCompletionSource gate, long version) =>
        writes.WriteAsync(
            "k",
            log,
            null,
            async (_, keeper) =>
            {
                await gate.Task;
                keeper.Returned("k", version);
                return true;
            },
            (l, _, keeper) => keeper.Local("k", l, x => Record(x, name + " set"), x => Record(x, name + " superseded"), version),
            Ct).AsTask();

    /// <summary>Timers that fire only when a test elapses them, so the drain timeout needs no real wait.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public TimeSpan[] Due
        {
            get
            {
                lock (_timers)
                {
                    return [.. _timers.Select(t => t.Due)];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void Elapse()
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private volatile bool _disposed;

            public TimeSpan Due => due;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
