using System.Diagnostics.Metrics;
using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Migration.Tests.Support;

namespace UiPath.Caching.Migration.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public sealed class MoverPaceTests(RedisFixture fixture)
{
    private const string MovedField = "moved";
    private const string OomSkipField = "skipped:oom";
    private const string HealthMetric = "move_health";
    private const string PauseMetric = "move_paused";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_health_source_that_says_red_pauses_the_runner_and_green_resumes_it()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        var keys = Enumerable.Range(0, 20).Select(i => $"k{i}").ToArray();
        await kit.SeedAsync(keys);
        var health = new MoveRateTests.FakeHealth { State = KeyMoveHealthState.Red };
        var host = kit.Host(rate: Fast(health, new MoveRateTests.FakeMemory()));
        host.Start();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);

            (await kit.Db.HashLengthAsync(kit.Kit.Names.Progress(kit.Kit.Group))).Should().Be(0, "nothing is published while the health is red");
            (await kit.Kit.ExistsAsync(kit.Kit.N("k0"))).Should().BeFalse();
            (await kit.Kit.ExistsAsync(kit.Kit.L("k0"))).Should().BeTrue();

            health.State = KeyMoveHealthState.Green;
            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.ProgressAsync())[MovedField].Should().Be(20);
    }

    [Theory]
    [InlineData("memory_ceiling")]
    [InlineData("memory_growth")]
    [InlineData("evicted")]
    [InlineData("noeviction")]
    public async Task A_memory_rule_stops_every_publish_while_it_holds_and_the_runner_goes_on_when_it_lifts(string reason)
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 20).Select(i => $"k{i}").ToArray());
        var memory = new MoveRateTests.FakeMemory { Reason = reason };
        var host = kit.Host(rate: Fast(new MoveRateTests.FakeHealth(), memory));
        host.Start();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);

            (await kit.Db.HashLengthAsync(kit.Kit.Names.Progress(kit.Kit.Group))).Should().Be(0);

            memory.Reason = null;
            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.ProgressAsync())[MovedField].Should().Be(20);
    }

    [Fact]
    public async Task A_pause_in_the_middle_of_a_batch_holds_the_unit_lease_and_stops_before_the_next_key()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 30).Select(i => $"k{i}").ToArray());
        var health = new MoveRateTests.FakeHealth();
        var counting = new SlowCounter(kit.Target);
        var rate = Fast(health, new MoveRateTests.FakeMemory());
        var host = kit.Host(rate: rate, wrap: _ => counting);
        var unit = (await kit.UnitIdsAsync()).Single();
        host.Start();
        try
        {
            await MoverKit.WaitAsync(() => Task.FromResult(counting.Total >= 5));
            health.State = KeyMoveHealthState.Red;
            await MoverKit.WaitAsync(async () => await rate.PauseReasonAsync(unit, Ct) == "red");
            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            var frozen = counting.Total;
            await Task.Delay(TimeSpan.FromMilliseconds(150), TimeProvider.System, Ct);

            counting.Total.Should().Be(frozen, "no key is moved while paused");
            frozen.Should().BeLessThan(30);
            (await kit.Db.KeyExistsAsync(kit.Kit.Names.UnitLease(kit.Kit.Group, unit))).Should().BeTrue("the pause renews the lease");

            health.State = KeyMoveHealthState.Green;
            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.ProgressAsync())[MovedField].Should().Be(30);
    }

    [Fact]
    public async Task An_OOM_reply_to_a_publish_backs_the_mover_off_and_the_key_is_tried_again_after_it()
    {
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => PublishFaultingDatabase.Over(db, out fault));
        await kit.SeedAsync(Enumerable.Range(0, 10).Select(i => $"k{i}").ToArray());
        fault!.Fail(Oom(), 1);
        var rate = Fast(new MoveRateTests.FakeHealth(), new MoveRateTests.FakeMemory(), o => o.OomBackoff = TimeSpan.FromMilliseconds(300));
        var host = kit.Host(rate: rate);
        var started = TimeProvider.System.GetTimestamp();
        var sawBackoff = false;
        host.Start();
        try
        {
            await MoverKit.WaitAsync(async () =>
            {
                sawBackoff |= await rate.PauseReasonAsync("probe", Ct) == "oom_backoff";
                return await kit.PassedAsync();
            });
        }
        finally
        {
            await host.StopAsync();
        }

        TimeProvider.System.GetElapsedTime(started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250), "the mover stopped for the backoff");
        sawBackoff.Should().BeTrue();
        fault.Faulted.Should().Be(1);
        var progress = await kit.ProgressAsync();
        progress[MovedField].Should().Be(10, "the refused key was published after the backoff");
        progress.Should().NotContainKey(OomSkipField);
    }

    [Fact]
    public async Task A_second_OOM_on_the_same_key_leaves_it_for_the_next_pass_and_is_counted()
    {
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => PublishFaultingDatabase.Over(db, out fault));
        await kit.SeedAsync(Enumerable.Range(0, 10).Select(i => $"k{i}").ToArray());
        fault!.Fail(Oom(), 2);
        var host = kit.Host(rate: Fast(new MoveRateTests.FakeHealth(), new MoveRateTests.FakeMemory(), o => o.OomBackoff = TimeSpan.FromMilliseconds(100)));

        await RunBatchAsync(kit, host, OomSkipField);

        var progress = await kit.ProgressAsync();
        progress[MovedField].Should().Be(9);
        progress[OomSkipField].Should().Be(1);
    }

    [Fact]
    public async Task A_timeout_is_counted_as_one_for_the_health_source_and_the_key_waits_for_the_next_pass()
    {
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => PublishFaultingDatabase.Over(db, out fault));
        await kit.SeedAsync(Enumerable.Range(0, 10).Select(i => $"k{i}").ToArray());
        fault!.Fail(new TimeoutException("Timeout performing EVAL"), 1);
        var latency = new MoverLatency(TimeProvider.System);
        var rate = new AimdMoveRate(new MoveRateTests.FakeHealth(), null, Quiet(), TimeProvider.System, latency);
        var host = kit.Host(rate: rate);

        await RunBatchAsync(kit, host, "skipped:timeout");

        var progress = await kit.ProgressAsync();
        progress[MovedField].Should().Be(9);
        progress["skipped:timeout"].Should().Be(1);
        latency.TakeTimeouts().Should().Be(1);
    }

    [Fact]
    public void The_router_keeps_no_latency_histogram_so_nothing_the_mover_does_can_reach_one()
    {
        RouterTelemetry.CountMoverHealth("green");
        var histograms = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == RouterTelemetry.MeterName && instrument.GetType().GetGenericTypeDefinition() == typeof(Histogram<>))
            {
                histograms.Add(instrument.Name);
            }
        };
        listener.Start();

        histograms.Should().BeEmpty("the health source leaves the application p99 out until the router has a histogram fed by non-mover calls; a histogram added later must never be fed by the mover");
    }

    [Fact]
    public async Task The_counters_name_the_pause_the_health_state_and_the_rate_changes_with_bounded_tags()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<(string Name, string Tag)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RouterTelemetry.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                seen.Add((instrument.Name, $"{tag.Key}={tag.Value}"));
            }
        });
        listener.Start();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero));
        var health = new MoveRateTests.FakeHealth { State = KeyMoveHealthState.Amber };
        var rate = new AimdMoveRate(health, null, new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions()), time);
        await rate.PauseReasonAsync("u1", Ct);
        health.State = KeyMoveHealthState.Red;
        time.Advance(TimeSpan.FromSeconds(10));
        await rate.PauseReasonAsync("u1", Ct);

        seen.Should().Contain((HealthMetric, "state=amber"));
        seen.Should().Contain((HealthMetric, "state=red"));
        seen.Should().Contain(("move_rate_changes", "direction=down"));
        seen.Should().Contain((PauseMetric, "reason=red"));
        seen.Where(s => s.Name is PauseMetric or HealthMetric or "move_rate_changes").Select(s => s.Tag).Should().OnlyContain(t => t.StartsWith("reason=", StringComparison.Ordinal) || t.StartsWith("state=", StringComparison.Ordinal) || t.StartsWith("direction=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_key_is_reserved_before_it_is_captured_and_the_bytes_of_each_value_are_paid_before_the_next_key()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 12).ToDictionary(i => $"k{i}", i => (new string('x', 1000), (TimeSpan?)null)));
        var rate = new RecordingRate();
        var checking = new ChargedFirst(kit.Target, rate);
        var host = kit.Host(rate: rate, wrap: _ => checking);

        await kit.RunOnePassAsync(host);

        checking.Uncharged.Should().Be(0, "no key was captured before its reservation, and the bytes of the values before it were paid");
        rate.Keys.Should().Be(12, "a pass of one batch, which is also the final one, is charged in full");
        rate.Bytes.Should().BeGreaterThanOrEqualTo(12_000);
        rate.Bytes.Should().Be(checking.Bytes, "every byte published is charged once, the last value's before the cursor moves on");
        (await kit.ProgressAsync())[MovedField].Should().Be(12);
    }

    [Fact]
    public async Task A_value_whose_debt_outlasts_the_fence_is_published_and_the_debt_is_paid_before_the_next_key_is_captured()
    {
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => PublishFaultingDatabase.Over(db, out fault), moveOptions: new KeyMoveOptions { FenceTtl = TimeSpan.FromMilliseconds(300) });
        await kit.SeedAsync(Enumerable.Range(0, 4).ToDictionary(i => $"k{i}", i => (new string('x', 4000), (TimeSpan?)null)));

        // A byte costs a tenth of a millisecond: a value of 4000 bytes asks for some 400 ms, longer than the 300 ms life of the fence.
        var rate = new RecordingRate { MillisecondsPer10Bytes = 1 };
        var started = TimeProvider.System.GetTimestamp();
        var published = new System.Collections.Concurrent.ConcurrentQueue<TimeSpan>();
        fault!.AfterPublish = () => published.Enqueue(TimeProvider.System.GetElapsedTime(started));
        var starts = new StartTimes(kit.Target, started);
        var host = kit.Host(rate: rate, wrap: _ => starts);

        await kit.RunOnePassAsync(host);

        var progress = await kit.ProgressAsync();
        progress[MovedField].Should().Be(4, "the wait never sits between the capture and the publish, so the fence is alive at every publish");
        progress.Keys.Should().NotContain(k => k.StartsWith("skipped:", StringComparison.Ordinal));
        var order = starts.Times.OrderBy(t => t).ToArray();
        var done = published.OrderBy(t => t).ToArray();
        done.Should().HaveCount(4);
        for (var i = 1; i < 4; i++)
        {
            (order[i] - done[i - 1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350), "the debt of the value before is paid before the next key is captured");
        }
    }

    [Fact]
    public async Task A_restart_after_a_refused_publish_and_an_OOM_retry_charge_each_key_once()
    {
        MoverKit? made = null;
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(
            fixture,
            interceptDb: db => PublishFaultingDatabase.Over(db, out fault),
            schedule: new WriterAtPublish(async () => await made!.Db.StringSetAsync(made.Kit.Names.ForwardFence(made.Kit.N("k0")), "moved-by-a-writer")));
        made = kit;
        await kit.SeedAsync("k0");

        // The first publish finds the fence moved; the second is refused by the server for memory; the third goes through.
        var armed = 0;
        fault!.BeforePublish = () =>
        {
            if (Interlocked.Exchange(ref armed, 1) == 0)
            {
                fault.Fail(Oom(), 1);
            }
        };
        var rate = new RecordingRate();
        var checking = new ChargedFirst(kit.Target, rate);
        var host = kit.Host(rate: rate, wrap: _ => checking, options: MoverKit.Quick);

        await kit.RunOnePassAsync(host);

        (await kit.ProgressAsync())[MovedField].Should().Be(1);
        fault.Faulted.Should().Be(1);
        rate.Bytes.Should().Be(checking.Bytes, "a key whose publish was refused, or refused by the server for memory, is charged for the one value that was moved");
    }

    [Fact]
    public async Task A_stop_right_after_a_publish_leaves_every_value_but_the_last_charged()
    {
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => PublishFaultingDatabase.Over(db, out fault));
        await kit.SeedAsync(Enumerable.Range(0, 8).ToDictionary(i => $"k{i}", i => (new string('x', 4000), (TimeSpan?)null)));
        var rate = new RecordingRate();
        var host = kit.Host(rate: rate);
        Task? stopping = null;
        fault!.AfterPublish = () => stopping ??= Task.Run(host.StopAsync, CancellationToken.None);
        host.Start();
        await MoverKit.WaitAsync(() => Task.FromResult(stopping is not null));
        await stopping!;

        var published = 0;
        long size = 0;
        for (var i = 0; i < 8; i++)
        {
            if (await kit.Kit.ExistsAsync(kit.Kit.N($"k{i}")))
            {
                published++;
                size = await kit.Db.StringLengthAsync(kit.Kit.N($"k{i}"));
            }
        }

        published.Should().BeGreaterThan(0);
        size.Should().BeGreaterThanOrEqualTo(4000);
        rate.Bytes.Should().BeGreaterThanOrEqualTo((published - 1) * size, "the burst a stop can leave unpaid is one value");
    }

    [Fact]
    public async Task Repeated_interruptions_and_a_handover_to_a_new_runner_on_the_node_still_pay_every_published_value()
    {
        PublishFaultingDatabase? fault = null;
        await using var kit = await MoverKit.CreateAsync(fixture, interceptDb: db => PublishFaultingDatabase.Over(db, out fault));
        await kit.SeedAsync(Enumerable.Range(0, 8).ToDictionary(i => $"k{i}", i => (new string('x', 4000), (TimeSpan?)null)));

        // One rate for the node, as in the process; each round is a new host with new runners, stopped right after a publish.
        var rate = new RecordingRate();
        var published = 0;
        for (var round = 0; round < 3; round++)
        {
            var host = kit.Host(rate: rate);
            Task? stopping = null;
            fault!.AfterPublish = () =>
            {
                Interlocked.Increment(ref published);
                stopping ??= Task.Run(host.StopAsync, CancellationToken.None);
            };
            host.Start();
            await MoverKit.WaitAsync(() => Task.FromResult(stopping is not null));
            await stopping!;
        }

        published.Should().BeGreaterThanOrEqualTo(3);
        (rate.Bytes + rate.Owed).Should().BeGreaterThanOrEqualTo(published * 4000L, "a value published by a runner that was stopped is still owed by the node, and paid by whoever takes the unit");

        fault!.AfterPublish = null;
        await kit.RunOnePassAsync(kit.Host(rate: rate));

        rate.Owed.Should().Be(0, "the runner that took the unit paid the debt before its next capture");
        rate.Bytes.Should().BeGreaterThanOrEqualTo(8 * 4000L);
    }

    [Fact]
    public async Task The_default_health_stack_stays_green_while_the_mover_is_paced_hard_because_the_waits_are_not_in_its_p99()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 24).ToDictionary(i => $"k{i:D2}", i => (new string('x', 4000), (TimeSpan?)null)));
        var options = new StaticOptionsMonitor<KeyMoveMoverOptions>(new KeyMoveMoverOptions
        {
            StartKeysPerSecond = 1_000_000,
            StartBytesPerSecond = 20_000,
            MinBytesPerSecond = 20_000,
            MaxBytesPerSecond = 20_000,
            EvaluationInterval = TimeSpan.FromMilliseconds(20),
            MoverP99Amber = TimeSpan.FromMilliseconds(100),
            MoverP99Red = TimeSpan.FromMilliseconds(150),
        });
        var latency = new MoverLatency(TimeProvider.System);
        var watch = new RedisInfoWatch(_ => Task.FromResult<IReadOnlyList<NodeInfo>>([new NodeInfo("u1", false, 1024, 0, "allkeys-lru", 0, 1, 100, null)]), options, TimeProvider.System);
        var rate = new AimdMoveRate(new RedisInfoHealthSource(watch, latency, options), watch, options, TimeProvider.System, latency);
        var unit = (await kit.UnitIdsAsync()).Single();
        var host = kit.Host(rate: rate);
        var started = TimeProvider.System.GetTimestamp();

        await kit.RunOnePassAsync(host);

        TimeProvider.System.GetElapsedTime(started).Should().BeGreaterThan(TimeSpan.FromSeconds(3), "each value waits some 200 ms at 20 KB a second, longer than the red limit of the p99");
        latency.P99(TimeSpan.FromMinutes(1)).Should().NotBeNull().And.BeLessThan(TimeSpan.FromMilliseconds(100), "the samples are the time Redis took, not the waits");
        rate.RateOf(unit).BytesPerSecond.Should().Be(20_000);
        (await rate.PauseReasonAsync(unit, Ct)).Should().BeNull("the health stayed green: the pace did not halve the rate or pause the unit");
        (await kit.ProgressAsync())[MovedField].Should().Be(24);
    }

    [Fact]
    public async Task A_unit_paced_for_more_than_a_few_polls_shows_the_pacing_pause_and_a_short_pace_does_not()
    {
        var pacing = new System.Collections.Concurrent.ConcurrentBag<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RouterTelemetry.MeterName && instrument.Name == PauseMetric)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag is { Key: "reason", Value: "pacing" })
                {
                    pacing.Add(value);
                }
            }
        });
        listener.Start();

        // Under four polls of 40 ms.
        await using (var shortKit = await MoverKit.CreateAsync(fixture))
        {
            await shortKit.SeedAsync(Enumerable.Range(0, 3).Select(i => $"k{i}").ToArray());
            await shortKit.RunOnePassAsync(shortKit.Host(rate: new RecordingRate { FirstDelay = TimeSpan.FromMilliseconds(100) }));
        }

        pacing.Should().BeEmpty("a wait of a few polls is not a pause");

        await using var longKit = await MoverKit.CreateAsync(fixture);
        await longKit.SeedAsync(Enumerable.Range(0, 3).Select(i => $"k{i}").ToArray());
        var rate = new RecordingRate { FirstDelay = TimeSpan.FromMilliseconds(600) };
        await longKit.RunOnePassAsync(longKit.Host(rate: rate));

        pacing.Should().ContainSingle("one wait is reported once, not on every slice");
        rate.PauseChecks.Should().BeGreaterThan(8, "the pause is looked at on every slice of a wait, so one that begins during it is seen at once");
    }

    [Fact]
    public async Task A_wait_in_progress_follows_what_the_rate_says_is_left()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 3).Select(i => $"k{i}").ToArray());
        var rate = new RecordingRate { FirstDelay = TimeSpan.FromSeconds(30), Left = TimeSpan.Zero };
        var started = TimeProvider.System.GetTimestamp();

        await kit.RunOnePassAsync(kit.Host(rate: rate));

        TimeProvider.System.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(15), "a reload that raised the rate ended the wait of 30 s at the next slice");
        (await kit.ProgressAsync())[MovedField].Should().Be(3);
    }

    [Fact]
    public async Task A_pace_of_one_slice_still_asks_whether_the_state_allows_the_unit_to_go_on()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 5).Select(i => $"k{i}").ToArray());

        // A poll of 5 s keeps the host's own look at the state out of the 20 ms of the pace: the only look in it is the runner's.
        var reads = 0;
        var atReserve = -1;
        var atCapture = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rate = new RecordingRate { FirstDelay = TimeSpan.FromMilliseconds(20), OnFirst = () => atReserve = Volatile.Read(ref reads) };
        var host = kit.Host(
            rate: rate,
            options: MoverKit.Quick with { PollInterval = TimeSpan.FromSeconds(5) },
            readState: _ =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<StateRecord?>(kit.State());
            },
            wrap: inner => new FirstMove(inner, () => atCapture.TrySetResult(Volatile.Read(ref reads))));
        host.Start();
        try
        {
            var seen = await atCapture.Task.WaitAsync(TimeSpan.FromSeconds(20), TimeProvider.System, Ct);

            atReserve.Should().BeGreaterThanOrEqualTo(0);
            seen.Should().BeGreaterThan(atReserve, "a pace shorter than one poll is followed by a look at the state before the key is captured");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Two_runners_with_large_values_keep_to_one_byte_cap()
    {
        await using var first = await MoverKit.CreateAsync(fixture);
        await using var second = await MoverKit.CreateAsync(fixture);
        var big = Enumerable.Range(0, 5).ToDictionary(i => $"k{i}", i => (new string('x', 40_000), (TimeSpan?)null));
        await first.SeedAsync(big);
        await second.SeedAsync(big);

        // 400 KB in all at 200 KB a second under one cap take 2 s; two caps, one for each runner, would take 1 s.
        var shared = Fast(new MoveRateTests.FakeHealth(), new MoveRateTests.FakeMemory(), o => o.GlobalBytesPerSecond = 200_000);
        var hostA = first.Host(rate: shared);
        var hostB = second.Host(rate: shared);
        var started = TimeProvider.System.GetTimestamp();
        hostA.Start();
        hostB.Start();
        try
        {
            await MoverKit.WaitAsync(async () => await first.PassedAsync() && await second.PassedAsync());
        }
        finally
        {
            await hostA.StopAsync();
            await hostB.StopAsync();
        }

        TimeProvider.System.GetElapsedTime(started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1700));
    }

    [Fact]
    public async Task A_pace_longer_than_the_lease_renews_the_lease_so_no_takeover_happens()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 5).Select(i => $"k{i}").ToArray());
        var unit = (await kit.UnitIdsAsync()).Single();
        var rate = new RecordingRate { FirstDelay = TimeSpan.FromMilliseconds(1800) };
        var host = kit.Host(rate: rate, options: MoverKit.Quick with { LeaseTtl = TimeSpan.FromMilliseconds(600) });
        host.Start();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1300), TimeProvider.System, Ct);

            (await kit.Db.KeyExistsAsync(kit.Kit.Names.UnitLease(kit.Kit.Group, unit))).Should().BeTrue("the pace renews the lease in slices, three times the lease long");
            (await kit.Kit.ExistsAsync(kit.Kit.N("k0"))).Should().BeFalse("nothing is published before the reservation has been waited out");

            await MoverKit.WaitAsync(kit.PassedAsync);
        }
        finally
        {
            await host.StopAsync();
        }

        var progress = await kit.ProgressAsync();
        progress[MovedField].Should().Be(5);
        progress.Should().NotContainKey("lease_takeovers", "the lease never lapsed");
    }

    [Fact]
    public async Task A_pace_in_which_ownership_is_lost_ends_the_pass_with_no_write()
    {
        await using var kit = await MoverKit.CreateAsync(fixture);
        await kit.SeedAsync(Enumerable.Range(0, 5).Select(i => $"k{i}").ToArray());
        var acked = true;
        var rate = new RecordingRate { FirstDelay = TimeSpan.FromMilliseconds(1500) };
        var host = kit.Host(rate: rate, state: () => kit.State("S3", acked));
        host.Start();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), TimeProvider.System, Ct);
            acked = false;
            await Task.Delay(TimeSpan.FromMilliseconds(2000), TimeProvider.System, Ct);
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.Kit.ExistsAsync(kit.Kit.N("k0"))).Should().BeFalse("the acknowledgement was withdrawn while the unit waited, so nothing is published after");
        (await kit.Kit.ExistsAsync(kit.Kit.L("k0"))).Should().BeTrue();
        (await kit.ProgressAsync()).GetValueOrDefault(MovedField).Should().Be(0);
    }

    [Fact]
    public async Task Two_hosts_that_share_one_rate_keep_to_one_cap_of_the_node()
    {
        await using var first = await MoverKit.CreateAsync(fixture);
        await using var second = await MoverKit.CreateAsync(fixture);
        await first.SeedAsync(Enumerable.Range(0, 15).Select(i => $"k{i}").ToArray());
        await second.SeedAsync(Enumerable.Range(0, 15).Select(i => $"k{i}").ToArray());

        // 30 keys at 50 a second under one cap take 0.6 s; two caps of 50, one for each host, would take 0.3 s.
        var shared = Fast(new MoveRateTests.FakeHealth(), new MoveRateTests.FakeMemory(), o => o.GlobalKeysPerSecond = 50);
        var hostA = first.Host(rate: shared);
        var hostB = second.Host(rate: shared);
        var started = TimeProvider.System.GetTimestamp();
        hostA.Start();
        hostB.Start();
        try
        {
            await MoverKit.WaitAsync(async () => await first.PassedAsync() && await second.PassedAsync());
        }
        finally
        {
            await hostA.StopAsync();
            await hostB.StopAsync();
        }

        TimeProvider.System.GetElapsedTime(started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(550));
        (await first.ProgressAsync())[MovedField].Should().Be(15);
        (await second.ProgressAsync())[MovedField].Should().Be(15);
    }

    // A key the server refused is not an accepted class, so the pass leaves no finished record: the batch is done when its counters are in.
    private static async Task RunBatchAsync(MoverKit kit, MoverHost host, string field)
    {
        host.Start();
        try
        {
            await MoverKit.WaitAsync(async () => await kit.ProgressAsync() is { } p && p.ContainsKey(field) && p.GetValueOrDefault(MovedField) == 9);
        }
        finally
        {
            await host.StopAsync();
        }

        (await kit.PassedAsync()).Should().BeFalse("a pass with a failed key is not a finished pass");
    }

    private static RedisServerException Oom() => new(RedisErrorKind.None, CommandFlags.None, "OOM command not allowed when used memory > 'maxmemory'.");

    private static StaticOptionsMonitor<KeyMoveMoverOptions> Quiet(Action<KeyMoveMoverOptions>? configure = null)
    {
        // Fast enough that a test is never waiting on the pace, and a reading every 20 ms so a change of the fakes shows at once.
        var options = new KeyMoveMoverOptions
        {
            StartKeysPerSecond = 1_000_000,
            StartBytesPerSecond = 1e12,
            EvaluationInterval = TimeSpan.FromMilliseconds(20),
        };
        configure?.Invoke(options);
        return new StaticOptionsMonitor<KeyMoveMoverOptions>(options);
    }

    private static AimdMoveRate Fast(IKeyMoveHealthSource health, IMoverMemory memory, Action<KeyMoveMoverOptions>? configure = null) =>
        new(health, memory, Quiet(configure), TimeProvider.System);

    // Charges are recorded, and the first reservation can ask for a long wait.
    private sealed class RecordingRate : IMoveRate
    {
        private int _keys;
        private long _bytes;
        private int _first;
        private int _pauseChecks;
        private long _owed;

        public TimeSpan FirstDelay { get; init; }

        // What the rate says is left of a wait in progress; null leaves the count to the runner.
        public TimeSpan? Left { get; init; }

        // The wait of each reservation, in milliseconds for each 10 bytes.
        public int MillisecondsPer10Bytes { get; init; }

        public Action? OnFirst { get; init; }

        public int Keys => Volatile.Read(ref _keys);

        public long Bytes => Interlocked.Read(ref _bytes);

        public int PauseChecks => Volatile.Read(ref _pauseChecks);

        // Bytes published and not yet reserved.
        public long Owed => Interlocked.Read(ref _owed);

        public void Owe(string unit, long bytes) => Interlocked.Add(ref _owed, bytes);

        public long TakeDebt(string unit) => Interlocked.Exchange(ref _owed, 0);

        public ValueTask<string?> PauseReasonAsync(string unit, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _pauseChecks);
            return ValueTask.FromResult<string?>(null);
        }

        public TimeSpan Reserve(string unit, int keys, long bytes)
        {
            Interlocked.Add(ref _keys, keys);
            Interlocked.Add(ref _bytes, bytes);
            if (Interlocked.Exchange(ref _first, 1) == 0)
            {
                OnFirst?.Invoke();
                return FirstDelay + TimeSpan.FromMilliseconds(bytes / 10.0 * MillisecondsPer10Bytes);
            }

            return TimeSpan.FromMilliseconds(bytes / 10.0 * MillisecondsPer10Bytes);
        }

        public TimeSpan? Remaining(string unit) => Left;

        public void Observed(string unit, MoveResult result, TimeSpan elapsed)
        {
        }
    }

    // Counts the keys that are captured before a reservation has been made for them, or before the bytes published ahead of them were paid, and the bytes published.
    private sealed class ChargedFirst(IMoveTarget inner, RecordingRate rate) : IMoveTarget
    {
        private int _handled;
        private int _uncharged;
        private long _bytes;

        public int Uncharged => Volatile.Read(ref _uncharged);

        public long Bytes => Interlocked.Read(ref _bytes);

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
        {
            if (rate.Keys < Interlocked.Increment(ref _handled) || rate.Bytes < Interlocked.Read(ref _bytes))
            {
                Interlocked.Increment(ref _uncharged);
            }

            var result = await inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
            Interlocked.Add(ref _bytes, result.Bytes);
            return result;
        }
    }

    // Tells when the first key's promotion begins.
    private sealed class FirstMove(IMoveTarget inner, Action first) : IMoveTarget
    {
        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
        {
            first();
            return inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
        }
    }

    // When each key's promotion began.
    private sealed class StartTimes(IMoveTarget inner, long started) : IMoveTarget
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<TimeSpan> _times = new();

        public IReadOnlyCollection<TimeSpan> Times => _times;

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
        {
            _times.Enqueue(TimeProvider.System.GetElapsedTime(started));
            return inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
        }
    }

    // Counts the keys the mover handles and takes a moment for each, so a test can act in the middle of a batch.
    private sealed class SlowCounter(IMoveTarget inner) : IMoveTarget
    {
        private int _total;

        public int Total => Volatile.Read(ref _total);

        public string CacheId => inner.CacheId;

        public bool NoOrigin => inner.NoOrigin;

        public async ValueTask<MoveResult> MoveAsync(string key, TimeSpan minTtl, string identity, IMoveOwnership ownership, CancellationToken cancellationToken)
        {
            var result = await inner.MoveAsync(key, minTtl, identity, ownership, cancellationToken);
            Interlocked.Increment(ref _total);
            await Task.Delay(TimeSpan.FromMilliseconds(15), TimeProvider.System, cancellationToken);
            return result;
        }
    }
}
