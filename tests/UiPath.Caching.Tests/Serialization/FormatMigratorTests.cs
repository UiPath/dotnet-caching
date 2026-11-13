using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UiPath.Caching.Serialization;
using UiPath.Caching.Tests.Fakes;
using UiPath.Caching.Tests.Telemetry;

namespace UiPath.Caching.Tests.Serialization;

public class FormatMigratorTests
{
    private static readonly object Owner = new();
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly RecordingTelemetryProvider _telemetry = new();
    private readonly ManualTimeProvider _clock = new(Start);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_offered_job_runs_off_the_callers_thread_and_is_counted_as_done()
    {
        using var sut = Migrator();
        var caller = Environment.CurrentManagedThreadId;
        var ranOn = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        sut.TryEnqueue(Owner, "k", "Redis", 1, ranOn, static tcs => _ =>
        {
            tcs.SetResult(Environment.CurrentManagedThreadId);
            return ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done));
        }).Should().BeTrue();

        (await ranOn.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().NotBe(caller);
        await WaitForAsync(() => Outcomes("done") == 1);
    }

    [Fact]
    public async Task A_job_is_not_started_by_the_call_that_offers_it()
    {
        using var sut = Migrator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var returned = sut.TryEnqueue(Owner, "k", "Redis", 1, release, static gate => async _ =>
        {
            await gate.Task;
            return RewriteTally.Of(RewriteOutcome.Done);
        });

        returned.Should().BeTrue();
        Outcomes("done").Should().Be(0);
        release.SetResult();
        await WaitForAsync(() => Outcomes("done") == 1);
    }

    [Theory]
    [InlineData("Done", "done")]
    [InlineData("Changed", "changed")]
    [InlineData("Lossy", "lossy")]
    [InlineData("Held", "held")]
    public async Task The_outcome_a_job_reports_is_the_tag_of_the_metric(string reported, string tag)
    {
        using var sut = Migrator();
        var outcome = Enum.Parse<RewriteOutcome>(reported);

        sut.TryEnqueue(Owner, "k", "RedisHash", 1, outcome, static o => _ => ValueTask.FromResult(RewriteTally.Of(o))).Should().BeTrue();

        await WaitForAsync(() => Outcomes(tag) == 1);
        var metric = _telemetry.Metrics.Single(m => m.Name == Metrics.SerializerRewrite);
        metric.Value.Should().Be(1);
        metric.Properties.Should().BeEquivalentTo(new Dictionary<string, string> { ["outcome"] = tag, ["cache"] = "RedisHash" });
    }

    [Fact]
    public async Task A_job_that_carried_several_values_counts_each_outcome_by_how_many_values_had_it()
    {
        using var sut = Migrator();

        sut.TryEnqueue(Owner, "hash", "RedisHash", 1, 0, static _ => _ => ValueTask.FromResult(new RewriteTally(Done: 2, Changed: 1, Lossy: 3))).Should().BeTrue();

        await WaitForAsync(() => _telemetry.Metrics.Count(m => m.Name == Metrics.SerializerRewrite) == 3);
        _telemetry.Metrics.ToDictionary(m => m.Properties!["outcome"], m => m.Value).Should().BeEquivalentTo(
            new Dictionary<string, double> { ["done"] = 2, ["changed"] = 1, ["lossy"] = 3 });
    }

    [Fact]
    public async Task Fifty_concurrent_offers_of_one_key_start_one_job()
    {
        using var sut = Migrator();
        var starts = 0;
        var runs = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var accepted = Enumerable.Range(0, 50)
            .AsParallel()
            .Select(_ => sut.TryEnqueue(Owner, "hot", "Redis", 1, release, state =>
            {
                Interlocked.Increment(ref starts);
                return async _ =>
                {
                    Interlocked.Increment(ref runs);
                    await state.Task;
                    return RewriteTally.Of(RewriteOutcome.Done);
                };
            }))
            .ToArray();
        release.SetResult();
        await WaitForAsync(() => Outcomes("done") == 1);

        accepted.Count(a => a).Should().Be(1);
        starts.Should().Be(1);
        runs.Should().Be(1);
        _telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite).Should().ContainSingle();
    }

    [Fact]
    public async Task A_key_is_offered_again_once_the_window_has_passed()
    {
        using var sut = Migrator(window: TimeSpan.FromSeconds(10));
        bool Offer() => sut.TryEnqueue(Owner, "k", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done)));

        Offer().Should().BeTrue();
        await WaitForAsync(() => Outcomes("done") == 1);
        _clock.Advance(TimeSpan.FromSeconds(9));
        Offer().Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(2));

        Offer().Should().BeTrue("a window of ten seconds has passed");
        await WaitForAsync(() => Outcomes("done") == 2);
    }

    [Fact]
    public void A_refused_offer_does_not_start_the_job()
    {
        using var sut = Migrator(rate: 1);
        var starts = 0;
        Func<int, Func<CancellationToken, ValueTask<RewriteTally>>> start = _ =>
        {
            starts++;
            return _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done));
        };

        sut.TryEnqueue(Owner, "a", "Redis", 1, 0, start).Should().BeTrue();
        sut.TryEnqueue(Owner, "a", "Redis", 1, 0, start).Should().BeFalse("a duplicate");
        sut.TryEnqueue(Owner, "b", "Redis", 1, 0, start).Should().BeFalse("no token");

        starts.Should().Be(1);
    }

    [Fact]
    public void Offers_over_the_rate_are_counted_and_refilled_by_the_clock()
    {
        using var sut = Migrator(rate: 4);
        bool Offer(string key) => sut.TryEnqueue(Owner, key, "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done)));

        Enumerable.Range(0, 4).Select(i => Offer($"k{i}")).ToArray().Should().AllBeEquivalentTo(true);
        Offer("k4").Should().BeFalse();
        Offer("k5").Should().BeFalse();
        sut.Flush();
        Outcomes("ratelimited").Should().Be(2);

        _clock.Advance(TimeSpan.FromMilliseconds(250));
        Offer("k4").Should().BeTrue("a quarter of a second at 4 per second earns one token");
        Offer("k6").Should().BeFalse();
        sut.Flush();
        Outcomes("ratelimited").Should().Be(3);

        _clock.Advance(TimeSpan.FromSeconds(60));
        Enumerable.Range(10, 4).Select(i => Offer($"k{i}")).ToArray().Should().AllBeEquivalentTo(true);
        Offer("k20").Should().BeFalse("the bucket holds one second of tokens, however long it sat");
    }

    [Fact]
    public async Task A_key_refused_for_the_rate_is_admitted_when_a_token_comes_back()
    {
        using var sut = Migrator(rate: 1);
        bool Offer(string key) => sut.TryEnqueue(Owner, key, "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done)));

        Offer("a").Should().BeTrue();
        Offer("b").Should().BeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));

        Offer("b").Should().BeTrue();
        await WaitForAsync(() => Outcomes("done") == 2);
    }

    [Fact]
    public async Task A_full_queue_drops_the_offer_counts_it_and_forgets_the_key()
    {
        using var sut = Migrator(capacity: 3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Offer(string key) => sut.TryEnqueue(Owner, key, "Redis", 1, (release, running), static s => async _ =>
        {
            s.running.TrySetResult();
            await s.release.Task;
            return RewriteTally.Of(RewriteOutcome.Done);
        });

        Offer("a").Should().BeTrue();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Offer("b").Should().BeTrue();
        Offer("c").Should().BeTrue();
        Offer("d").Should().BeFalse();
        sut.Flush();
        Outcomes("dropped").Should().Be(1);

        release.SetResult();
        await WaitForAsync(() => Outcomes("done") == 3);
        Offer("d").Should().BeTrue("a dropped key is not remembered as admitted");
        await WaitForAsync(() => Outcomes("done") == 4);
    }

    [Fact]
    public async Task Jobs_run_one_at_a_time_in_the_order_they_were_offered()
    {
        using var sut = Migrator();
        var order = new List<int>();
        var inFlight = 0;
        var overlapped = false;

        for (var i = 0; i < 20; i++)
        {
            sut.TryEnqueue(Owner, $"k{i}", "Redis", 1, i, state => async _ =>
            {
                overlapped |= Interlocked.Increment(ref inFlight) > 1;
                await Task.Delay(1, Ct);
                order.Add(state);
                Interlocked.Decrement(ref inFlight);
                return RewriteTally.Of(RewriteOutcome.Done);
            }).Should().BeTrue();
        }

        await WaitForAsync(() => Outcomes("done") == 20);
        overlapped.Should().BeFalse();
        order.Should().Equal(Enumerable.Range(0, 20));
    }

    [Fact]
    public async Task A_job_that_throws_is_a_failure_and_the_next_job_still_runs()
    {
        var logger = new RecordedLogger();
        using var sut = Migrator(logger: logger);
        sut.TryEnqueue(Owner, "bad", "Redis", 1, 0, static _ => _ => throw new InvalidOperationException("boom")).Should().BeTrue();
        sut.TryEnqueue(Owner, "good", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeTrue();

        await WaitForAsync(() => Outcomes("done") == 1);

        Outcomes("failed").Should().Be(1);
        logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Warning);
        sut.Stopped.IsFaulted.Should().BeFalse();
    }

    [Fact]
    public void A_start_that_throws_is_a_failure_and_the_key_is_forgotten()
    {
        using var sut = Migrator();

        sut.TryEnqueue(Owner, "k", "Redis", 1, 0, static _ => throw new InvalidOperationException("boom")).Should().BeFalse();

        Outcomes("failed").Should().Be(1);
        sut.TryEnqueue(Owner, "k", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeTrue();
    }

    [Fact]
    public async Task Disposing_drops_what_is_queued_cancels_the_job_in_flight_and_ends_the_worker()
    {
        var sut = Migrator();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedRan = false;
        sut.TryEnqueue(Owner, "inflight", "Redis", 1, (running, canceled), static s => async token =>
        {
            s.running.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                s.canceled.SetResult();
                throw;
            }

            return RewriteTally.Of(RewriteOutcome.Done);
        }).Should().BeTrue();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sut.TryEnqueue(Owner, "queued", "Redis", 1, 0, _ => _ =>
        {
            queuedRan = true;
            return ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done));
        }).Should().BeTrue();

        sut.Dispose();

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await sut.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        queuedRan.Should().BeFalse();
        Outcomes("dropped").Should().Be(2, "the queued job and the one that was canceled");
        Outcomes("failed").Should().Be(0);
        Outcomes("done").Should().Be(0);
    }

    [Fact]
    public async Task The_worker_is_not_stopped_while_a_job_that_ignores_the_cancellation_is_still_running()
    {
        var sut = Migrator();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.TryEnqueue(Owner, "k", "Redis", 1, (running, finish), static s => async _ =>
        {
            s.running.SetResult();
            await s.finish.Task;
            return RewriteTally.Of(RewriteOutcome.Done);
        }).Should().BeTrue();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        sut.Dispose();

        sut.Stopped.IsCompleted.Should().BeFalse("the write in flight is finished, and its buffer returned, before the worker ends");
        finish.SetResult();
        await sut.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Outcomes("done").Should().Be(1, "a job that ran to its end is counted as it ended");
    }

    [Fact]
    public async Task Offers_after_disposal_are_refused_without_starting_a_job()
    {
        var sut = Migrator();
        sut.Dispose();
        var starts = 0;

        sut.TryEnqueue(Owner, "k", "Redis", 1, 0, _ =>
        {
            starts++;
            return _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done));
        }).Should().BeFalse();

        starts.Should().Be(0);
        await sut.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sut.Dispose();
    }

    [Fact]
    public async Task A_job_that_ends_after_disposal_with_an_error_leaves_no_unobserved_exception()
    {
        var unobserved = new List<Exception>();
        // Process-wide, so what another test left behind is not ours.
        void Observe(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains("late", StringComparison.Ordinal))
            {
                unobserved.Add(e.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            var sut = Migrator();
            var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sut.TryEnqueue(Owner, "k", "Redis", 1, running, static tcs => async token =>
            {
                tcs.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => { }, CancellationToken.None);
                throw new InvalidOperationException("late");
            }).Should().BeTrue();
            await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            sut.Dispose();
            await sut.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            unobserved.Should().BeEmpty();
            Outcomes("failed").Should().Be(1);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }
    }

    [Fact]
    public async Task A_key_that_came_out_lossy_is_not_offered_again_for_an_hour_and_one_that_did_not_is_offered_every_window()
    {
        using var sut = Migrator(window: TimeSpan.FromMinutes(1));
        bool Offer(string key, RewriteOutcome outcome) => sut.TryEnqueue(Owner, key, "Redis", 1, outcome, static o => _ => ValueTask.FromResult(RewriteTally.Of(o)));

        Offer("lossy", RewriteOutcome.Lossy).Should().BeTrue();
        Offer("changed", RewriteOutcome.Changed).Should().BeTrue();
        await WaitForAsync(() => Outcomes("lossy") == 1 && Outcomes("changed") == 1);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Offer("lossy", RewriteOutcome.Lossy).Should().BeFalse("it would only come out lossy again");
        Offer("changed", RewriteOutcome.Changed).Should().BeTrue();
        await WaitForAsync(() => Outcomes("changed") == 2);

        _clock.Advance(TimeSpan.FromMinutes(30));
        Offer("lossy", RewriteOutcome.Lossy).Should().BeFalse();
        _clock.Advance(TimeSpan.FromMinutes(30));
        Offer("lossy", RewriteOutcome.Lossy).Should().BeTrue("an hour has passed");
    }

    [Fact]
    public async Task Only_the_values_of_a_job_that_were_lossy_wait_an_hour()
    {
        using var sut = Migrator();
        var claimed = new[] { "a", "b", "c" }.Select(key => new FormatMigrator.OfferKey(Owner, key)).ToArray();
        foreach (var key in claimed)
        {
            sut.TryClaim(Owner, key.Key, "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed);
        }

        sut.EnqueueClaimed("Redis", claimed, 3, 0, static _ => _ => ValueTask.FromResult(new RewriteTally(1, 0, 1) { LossyAt = [1] })).Should().BeTrue();
        await WaitForAsync(() => Outcomes("lossy") == 1);
        _clock.Advance(TimeSpan.FromMinutes(5));

        sut.TryClaim(Owner, "a", "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed);
        sut.TryClaim(Owner, "b", "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Recent, "it is the one that was lossy");
        sut.TryClaim(Owner, "c", "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed);
    }

    [Fact]
    public void An_offer_refused_for_the_rate_takes_no_lock_until_a_token_is_due()
    {
        using var sut = Migrator(rate: 2);
        bool Offer(string key) => sut.TryEnqueue(Owner, key, "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done)));
        Offer("a").Should().BeTrue();
        Offer("b").Should().BeTrue();
        Offer("c").Should().BeFalse();
        var gated = sut.GatedClaims;

        for (var i = 0; i < 50; i++)
        {
            Offer($"r{i}").Should().BeFalse();
        }

        sut.GatedClaims.Should().Be(gated, "the bucket is known to be empty for half a second");
        sut.Flush();
        Outcomes("ratelimited").Should().Be(51, "the refusals are still counted");
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        Offer("c").Should().BeTrue("the token that came back is not hidden by the shortcut");
    }

    [Fact]
    public async Task An_offer_refused_for_a_full_queue_takes_no_lock_until_a_job_has_finished()
    {
        using var sut = Migrator(capacity: 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool Offer(string key) => sut.TryEnqueue(Owner, key, "Redis", 1, (release, running), static s => async _ =>
        {
            s.running.TrySetResult();
            await s.release.Task;
            return RewriteTally.Of(RewriteOutcome.Done);
        });
        Offer("a").Should().BeTrue();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Offer("b").Should().BeFalse();
        var gated = sut.GatedClaims;

        for (var i = 0; i < 50; i++)
        {
            Offer($"r{i}").Should().BeFalse();
        }

        sut.GatedClaims.Should().Be(gated);
        sut.Flush();
        Outcomes("dropped").Should().Be(51);
        release.SetResult();
        await sut.WhenIdleAsync(Ct);
        Offer("b").Should().BeTrue("a finished job makes room");
    }

    [Fact]
    public async Task A_lossy_entry_that_expired_before_the_sweep_and_is_claimed_again_is_not_counted_twice()
    {
        using var sut = Migrator(window: TimeSpan.FromMinutes(1));
        sut.TryEnqueue(Owner, "a", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Lossy))).Should().BeTrue();
        await sut.WhenIdleAsync(Ct);
        sut.LossyKeys.Should().Be(1);

        // The sweep ran 30 seconds before the hour was up, so the entry has expired but is still in the map.
        _clock.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(30));
        sut.TryClaim(Owner, "b", "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed);
        _clock.Advance(TimeSpan.FromSeconds(40));
        sut.TryClaim(Owner, "a", "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed);

        sut.LossyKeys.Should().Be(0, "the key is no longer held back as lossy");
    }

    [Fact]
    public void A_key_is_offered_again_after_a_long_idle_gap()
    {
        using var sut = Migrator(window: TimeSpan.FromSeconds(10));
        bool Offer(string key) => sut.TryEnqueue(Owner, key, "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done)));

        Offer("a").Should().BeTrue();
        Offer("b").Should().BeTrue();
        _clock.Advance(TimeSpan.FromSeconds(500));

        Offer("a").Should().BeTrue("only one window was ever remembered, however long nothing was offered");
        Offer("b").Should().BeTrue();
    }

    [Fact]
    public async Task The_same_key_name_of_another_cache_is_another_offer()
    {
        using var sut = Migrator();
        var other = new object();
        bool Offer(object owner) => sut.TryEnqueue(owner, "k", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done)));

        Offer(Owner).Should().BeTrue();
        Offer(Owner).Should().BeFalse();
        Offer(other).Should().BeTrue("a key of another cache or provider");
        await WaitForAsync(() => Outcomes("done") == 2);
    }

    [Fact]
    public async Task What_is_queued_is_bounded_by_bytes_and_a_refused_offer_makes_no_copy()
    {
        using var sut = Migrator(maxBytes: 100);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        bool Offer(string key, int bytes) => sut.TryEnqueue(Owner, key, "Redis", bytes, release, state =>
        {
            starts++;
            return async _ =>
            {
                await state.Task;
                return RewriteTally.Of(RewriteOutcome.Done);
            };
        });

        Offer("a", 60).Should().BeTrue();
        Offer("b", 60).Should().BeFalse("sixty and sixty do not fit in a hundred");
        Offer("c", 40).Should().BeTrue();
        starts.Should().Be(2, "the refused offer was never asked to make its copy");
        sut.Flush();
        Outcomes("dropped").Should().Be(1);

        release.SetResult();
        await WaitForAsync(() => Outcomes("done") == 2);
        Offer("b", 60).Should().BeTrue("the budget came back when the jobs ended");
        await WaitForAsync(() => Outcomes("done") == 3);
    }

    [Fact]
    public async Task A_busy_node_sends_one_metric_for_the_offers_it_refused_not_one_each()
    {
        using var sut = Migrator(rate: 1);
        sut.TryEnqueue(Owner, "first", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeTrue();
        for (var i = 0; i < 500; i++)
        {
            sut.TryEnqueue(Owner, $"k{i}", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeFalse();
        }

        _telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite && m.Properties!["outcome"] == "ratelimited").Should().BeEmpty("nothing is sent per read");
        _clock.Advance(TimeSpan.FromSeconds(1));

        var refused = _telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite && m.Properties!["outcome"] == "ratelimited").ToList();
        refused.Should().ContainSingle().Which.Value.Should().Be(500);
        await WaitForAsync(() => Outcomes("done") == 1);
    }

    [Fact]
    public void The_budget_and_the_key_come_back_when_the_job_could_not_be_made()
    {
        using var sut = Migrator(maxBytes: 100);

        sut.TryEnqueue(Owner, "a", "Redis", 80, 0, static _ => throw new InvalidOperationException("boom")).Should().BeFalse();

        sut.TryEnqueue(Owner, "b", "Redis", 80, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeTrue("the eighty bytes were given back");
        sut.TryEnqueue(Owner, "a", "Redis", 10, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeTrue("the key is not remembered as offered");
    }

    [Fact]
    public async Task A_rewrite_of_a_hundred_fields_that_fails_counts_a_hundred()
    {
        using var sut = Migrator();

        sut.EnqueueClaimed("Redis", Claim(sut, 100), 100, 0, static _ => _ => throw new InvalidOperationException("boom")).Should().BeTrue();

        await WaitForAsync(() => Outcomes("failed") == 100);
    }

    [Fact]
    public void A_rewrite_of_a_hundred_fields_that_cannot_be_made_counts_a_hundred()
    {
        using var sut = Migrator();

        sut.EnqueueClaimed("Redis", Claim(sut, 100), 100, 0, static _ => throw new InvalidOperationException("boom")).Should().BeFalse();

        Outcomes("failed").Should().Be(100);
    }

    [Fact]
    public void A_rewrite_of_a_hundred_fields_that_cannot_be_queued_counts_a_hundred_dropped()
    {
        var sut = Migrator();
        var claimed = Claim(sut, 100);
        sut.Dispose();

        sut.EnqueueClaimed("Redis", claimed, 100, 0, static _ => _ => ValueTask.FromResult(default(RewriteTally))).Should().BeFalse();

        Outcomes("dropped").Should().Be(100);
    }

    [Fact]
    public async Task A_rewrite_of_a_hundred_fields_in_flight_or_queued_at_shutdown_counts_a_hundred_each()
    {
        var sut = Migrator(rate: 1000);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.EnqueueClaimed("Redis", Claim(sut, 100, "inflight"), 100, running, static tcs => async token =>
        {
            tcs.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return default;
        }).Should().BeTrue();
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sut.EnqueueClaimed("Redis", Claim(sut, 100, "queued"), 100, 0, static _ => _ => ValueTask.FromResult(default(RewriteTally))).Should().BeTrue();

        sut.Dispose();
        await sut.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Outcomes("dropped").Should().Be(200, "the hundred in flight, cancelled, and the hundred behind it");
    }

    [Fact]
    public void A_token_comes_back_when_the_job_could_not_be_made()
    {
        using var sut = Migrator(rate: 1);

        sut.TryEnqueue(Owner, "a", "Redis", 1, 0, static _ => throw new InvalidOperationException("boom")).Should().BeFalse();

        sut.TryEnqueue(Owner, "b", "Redis", 1, 0, static _ => _ => ValueTask.FromResult(RewriteTally.Of(RewriteOutcome.Done))).Should().BeTrue("the token was given back");
    }

    [Fact]
    public async Task Failures_are_logged_once_an_interval_with_the_count_and_not_as_warnings_at_shutdown()
    {
        var logger = new RecordedLogger();
        var sut = Migrator(logger: logger);
        void Fail() => sut.TryEnqueue(Owner, Guid.NewGuid().ToString(), "Redis", 1, 0, static _ => _ => throw new InvalidOperationException("boom"));

        for (var i = 0; i < 20; i++)
        {
            Fail();
        }

        await WaitForAsync(() => Outcomes("failed") == 20);
        logger.Records.Where(r => r.Level == LogLevel.Warning).Should().ContainSingle();
        _clock.Advance(FormatMigrator.LogInterval + TimeSpan.FromSeconds(1));
        Fail();
        await WaitForAsync(() => Outcomes("failed") == 21);
        var warnings = logger.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        warnings.Should().HaveCount(2);
        warnings[1].Message.Should().Contain("19 more");

        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.TryEnqueue(Owner, "late", "Redis", 1, (running, proceed), static s => async _ =>
        {
            s.running.SetResult();
            await s.proceed.Task;
            throw new InvalidOperationException("the connection closed");
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _clock.Advance(FormatMigrator.LogInterval + TimeSpan.FromSeconds(1));
        sut.Dispose();
        proceed.SetResult();
        await sut.Stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        logger.Records.Where(r => r.Level == LogLevel.Warning).Should().HaveCount(2, "a failure while shutting down is not a warning");
        logger.Records.Should().Contain(r => r.Level == LogLevel.Debug);
    }

    [Fact]
    public void A_migrator_that_never_ran_a_job_disposes_cleanly()
    {
        var sut = Migrator();

        sut.Dispose();

        sut.Stopped.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static FormatMigrator.OfferKey[] Claim(FormatMigrator sut, int fields, string hash = "hash")
    {
        var claimed = Enumerable.Range(0, fields).Select(i => new FormatMigrator.OfferKey(Owner, $"{hash}\0f{i}")).ToArray();
        foreach (var key in claimed)
        {
            sut.TryClaim(Owner, key.Key, "Redis", 1).Should().Be(FormatMigrator.ClaimResult.Claimed);
        }

        return claimed;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true.");
            }

            await Task.Delay(5, Ct);
        }
    }

    private FormatMigrator Migrator(int capacity = 1024, int rate = 100, TimeSpan? window = null, ILogger? logger = null, long maxBytes = FormatMigrator.DefaultMaxQueuedBytes) =>
        new(_telemetry, _clock, logger ?? NullLogger.Instance, capacity, rate, window, maxBytes);

    private int Outcomes(string outcome) =>
        (int)_telemetry.Metrics.Where(m => m.Name == Metrics.SerializerRewrite && m.Properties!["outcome"] == outcome).Sum(m => m.Value);
}
