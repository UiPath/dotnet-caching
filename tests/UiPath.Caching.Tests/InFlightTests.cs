namespace UiPath.Caching.Tests;

public class InFlightTests
{
    private const string AbandonedFailure = "work failed after every caller left";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_callers_for_one_key_share_one_run()
    {
        var flights = new InFlight<string, int>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        var callers = Enumerable.Range(0, 10)
            .Select(_ => flights.RunAsync("k", release, (r, _) => { Interlocked.Increment(ref runs); return new ValueTask<int>(r.Task); }, Ct).AsTask())
            .ToArray();
        release.SetResult(42);

        (await Task.WhenAll(callers)).Should().AllBeEquivalentTo(42);
        runs.Should().Be(1);
    }

    [Fact]
    public async Task Different_keys_run_separately()
    {
        var flights = new InFlight<string, string>();

        var a = await flights.RunAsync("a", "A", static (s, _) => new ValueTask<string>(s), Ct);
        var b = await flights.RunAsync("b", "B", static (s, _) => new ValueTask<string>(s), Ct);

        (a, b).Should().Be(("A", "B"));
    }

    [Fact]
    public async Task A_caller_cancelled_before_it_arrives_starts_no_work()
    {
        var flights = new InFlight<string, int>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var runs = 0;

        var call = () => flights.RunAsync("k", 0, (_, _) => new ValueTask<int>(Interlocked.Increment(ref runs)), cancelled.Token).AsTask();

        await call.Should().ThrowAsync<OperationCanceledException>();
        runs.Should().Be(0);
        flights.Count.Should().Be(0);
    }


    [Fact]
    public async Task Work_every_caller_left_fails_without_an_unobserved_exception()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.InnerExceptions.Any(x => x.Message == AbandonedFailure))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonAFailingRunAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Volatile.Read(ref unobserved).Should().Be(0);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact]
    public async Task Work_every_caller_left_cannot_commit()
    {
        var flights = new InFlight<string, int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaving = new CancellationTokenSource();
        async ValueTask<int> IgnoreCancellation(TaskCompletionSource gate, IInFlightRun run)
        {
            await gate.Task;
            committed.SetResult(run.TryCommit(0, static _ => { }));
            return 0;
        }

        var abandoned = flights.RunAsync("k", release, IgnoreCancellation, leaving.Token).AsTask();
        await leaving.CancelAsync();
        await abandoned.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();

        (await committed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_finished_run_is_not_reused()
    {
        var flights = new InFlight<string, int>();
        var runs = 0;

        await flights.RunAsync("k", 0, (_, _) => new ValueTask<int>(Interlocked.Increment(ref runs)), Ct);
        var second = await flights.RunAsync("k", 0, (_, _) => new ValueTask<int>(Interlocked.Increment(ref runs)), Ct);

        second.Should().Be(2);
    }

    [Fact]
    public async Task A_failure_reaches_every_caller()
    {
        var flights = new InFlight<string, int>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, 3)
            .Select(_ => flights.RunAsync("k", release, static (r, _) => new ValueTask<int>(r.Task), Ct).AsTask())
            .ToArray();
        release.SetException(new InvalidOperationException("boom"));

        foreach (var caller in callers)
        {
            await caller.Invoking(c => c).Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task A_caller_that_cancels_leaves_the_run_going_for_the_others()
    {
        var flights = new InFlight<string, int>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workToken = CancellationToken.None;
        using var leaving = new CancellationTokenSource();

        var leaver = flights.RunAsync("k", release, (r, run) => { workToken = run.Token; return new ValueTask<int>(r.Task); }, leaving.Token).AsTask();
        var stayer = flights.RunAsync("k", release, static (r, _) => new ValueTask<int>(r.Task), Ct).AsTask();
        await leaving.CancelAsync();

        await leaver.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        workToken.IsCancellationRequested.Should().BeFalse();
        release.SetResult(7);
        (await stayer).Should().Be(7);
    }

    [Fact]
    public async Task The_run_is_cancelled_once_every_caller_has_cancelled()
    {
        var flights = new InFlight<string, int>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        static async ValueTask<int> UntilCancelled(TaskCompletionSource seen, IInFlightRun run)
        {
            await using (run.Token.Register(() => seen.TrySetResult()))
            {
                await Task.Delay(Timeout.Infinite, run.Token);
                return 0;
            }
        }

        var a = flights.RunAsync("k", cancelled, UntilCancelled, first.Token).AsTask();
        var b = flights.RunAsync("k", cancelled, UntilCancelled, second.Token).AsTask();
        await first.CancelAsync();
        cancelled.Task.IsCompleted.Should().BeFalse("one caller is still waiting");
        await second.CancelAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await a.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        await b.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_run_every_caller_left_leaves_the_table_even_if_its_work_ignores_cancellation()
    {
        var flights = new InFlight<string, int>();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaving = new CancellationTokenSource();

        var abandoned = flights.RunAsync("k", never, static (n, _) => new ValueTask<int>(n.Task), leaving.Token).AsTask();
        await leaving.CancelAsync();
        await abandoned.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        flights.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_caller_after_everyone_left_starts_a_fresh_run()
    {
        var flights = new InFlight<string, int>();
        var runs = 0;
        using var leaving = new CancellationTokenSource();
        async ValueTask<int> Hang(int _, IInFlightRun run)
        {
            Interlocked.Increment(ref runs);
            await Task.Delay(Timeout.Infinite, run.Token);
            return 0;
        }

        var abandoned = flights.RunAsync("k", 0, Hang, leaving.Token).AsTask();
        await leaving.CancelAsync();
        await abandoned.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();

        var fresh = await flights.RunAsync("k", 0, (_, _) => new ValueTask<int>(Interlocked.Increment(ref runs)), Ct);

        fresh.Should().Be(2);
    }

    // Not inlined, so nothing in the calling frame keeps the flight reachable when the test collects.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonAFailingRunAsync()
    {
        var flights = new InFlight<string, int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaving = new CancellationTokenSource();
        async ValueTask<int> FailLate(TaskCompletionSource gate, IInFlightRun run)
        {
            await gate.Task;
            failing.SetResult();
            throw new InvalidOperationException(AbandonedFailure);
        }

        var abandoned = flights.RunAsync("k", release, FailLate, leaving.Token).AsTask();
        await leaving.CancelAsync();
        await abandoned.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        await failing.Task;
        await Task.Delay(100, Ct);
    }
}
