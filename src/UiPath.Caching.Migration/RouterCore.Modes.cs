using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Broadcast;

namespace UiPath.Caching.Migration;

internal sealed partial class RouterCore
{
    private const int LibraryIndex = 0;
    private const int BypassIndex = 1;

    private static readonly TimeSpan SubscriptionPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FirstStartRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxStartRetry = TimeSpan.FromMinutes(1);

    // One counter per cache line: every call touches the one of its mode, and the two must not share a line.
    private readonly PaddedCounter[] _calls = new PaddedCounter[2];
    private readonly TaskCompletionSource?[] _callsDrained = new TaskCompletionSource?[2];
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly List<IRouterParticipant> _participants = [];
    private readonly ConcurrentQueue<(RouterMode Mode, RouterModeReason Reason)> _notifications = new();
    private readonly TaskCompletionSource _transitionsEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<CancellationToken, Task<LibraryParts>>? _libraryFactory;
    private LibraryParts? _started;
    private int _mode;
    private int _tierClosed;
    private int _flagSeen = -1;
    private int _requested;
    private int _libraryDown;
    private int _startLoop;
    private int _retryScheduled;
    private int _dispatching;
    private int _transitions;
    private int _unfinishedCleanup;

    public event Action<RouterMode, RouterModeReason>? ModeChanged;

    /// <summary>A live setting, read on every call; true means every call takes the source path.</summary>
    public Func<bool>? BypassFlag { get; init; }

    /// <summary>The longest a source-path call runs; one deadline of this length bounds a transition's waits and drain.</summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long router creation waits for the first library start before it runs on the source.</summary>
    public TimeSpan StartBound { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a cleanup step that ignores its token is waited for before the router moves on.</summary>
    public TimeSpan CleanupBound { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Completes once the router is stopped and every admitted transition has ended.</summary>
    public Task TransitionsEnded => _transitionsEnded.Task;

    /// <summary>Cleanup steps that did not finish in their bound and were left running.</summary>
    public int UnfinishedCleanup => Volatile.Read(ref _unfinishedCleanup);

    public CancellationToken StopToken => _stopping.Token;

    public RouterMode Mode => Volatile.Read(ref _mode) == BypassIndex ? RouterMode.BypassLibrary : RouterMode.Library;

    public RouterModeReason ModeReason => Mode == RouterMode.Library ? RouterModeReason.Default : CurrentReason();

    public bool LibraryStarted => Volatile.Read(ref _libraryDown) == 0;

    /// <summary>The parts of the library start in force; null while none is.</summary>
    internal LibraryParts? StartedParts => Volatile.Read(ref _started);

    /// <summary>The clock for the first library start bound. Null uses <see cref="TimeProvider.System"/>.</summary>
    internal TimeProvider? StartupClock { get; init; }

    private bool WantBypass => Volatile.Read(ref _requested) == 1 || Volatile.Read(ref _flagSeen) == 1 || !LibraryStarted;

    /// <summary>Admits one operation and decides its path, or throws before anything is sent when the router is stopped.</summary>
    public Admission Admit(string group, out Route route)
    {
        var admission = Admit();
        route = RoutePrecedence.Resolve(admission.Mode == BypassIndex || !LibraryStarted, !LibraryStarted, ReadFlag(), Volatile.Read(ref _tierClosed) == 0, useLibrary, group);
        return admission;
    }

    /// <summary>Admits an operation that has no path of its own, such as a clear.</summary>
    public Admission Admit()
    {
        Interlocked.Increment(ref _active);
        if (IsStopped)
        {
            ExitActive();
            throw new ObjectDisposedException(nameof(IRouter));
        }

        // A call counts in the mode it started in; one that sees the mode change under it counts again, so a drain never misses it.
        int mode;
        while (true)
        {
            mode = Volatile.Read(ref _mode);
            Interlocked.Increment(ref _calls[mode].Value);
            if (Volatile.Read(ref _mode) == mode)
            {
                break;
            }

            LeaveMode(mode);
        }

        return new Admission(this, mode);
    }

    public void Exit(int mode)
    {
        LeaveMode(mode);
        ExitActive();
    }

    public void Register(IRouterParticipant participant)
    {
        lock (_participants)
        {
            _participants.Add(participant);
        }
    }

    /// <summary>Makes the library a stack that can fail to start; until <see cref="Start"/> succeeds the router is in bypass.</summary>
    public void UseLibrary(Func<CancellationToken, Task<LibraryParts>> factory)
    {
        _libraryFactory = factory;
        Volatile.Write(ref _libraryDown, 1);
    }

    /// <summary>Starts the library in the background and waits at most <see cref="StartBound"/> for it; without it, or with the bypass flag on, the router starts in bypass.</summary>
    public void Start()
    {
        var flag = BypassFlag is null ? FlagState.Off : ReadFlag(notify: false);
        if (_libraryFactory is null)
        {
            if (flag != FlagState.Off)
            {
                BeginBypass();
            }

            return;
        }

        if (flag != FlagState.Off)
        {
            BeginBypass();
            EnsureLibraryStarting(immediate: false);
            return;
        }

        EnsureLibraryStarting(immediate: true);
        BoundedStop.CompletesAsync(_firstAttempt.Task, StartBound, StartupClock ?? TimeProvider.System).GetAwaiter().GetResult();

        if (!LibraryStarted)
        {
            BeginBypass();
            Run(ReconcileInBackground);
        }
    }

    public async Task EnterBypassAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _requested, 1);
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task LeaveBypassAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _requested, 0);
        try
        {
            if (!await ReconcileAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("The router stayed in bypass: the library subscriptions or pending writes did not settle within the source timeout. It keeps trying in the background.");
            }
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _requested, 1);
            throw;
        }
    }

    /// <summary>Stops what the library started and removes this node's topics and consumer groups; each step is bounded and none throws.</summary>
    public async Task ReleaseLibraryAsync()
    {
        var parts = Interlocked.Exchange(ref _started, null);
        if (parts is null)
        {
            return;
        }

        await StopHostedAsync(parts).ConfigureAwait(false);
        foreach (var participant in Snapshot())
        {
            if (participant.Topic is { } topic)
            {
                using var bound = new CancellationTokenSource(CleanupBound, Clock);
                try
                {
                    try
                    {
                        await BoundedAsync(parts.ReleaseTopicAsync(topic, bound.Token), "topic release").ConfigureAwait(false);
                    }
                    finally
                    {
                        // A step that outlasted its bound is cut off here, not left running after the release has moved on.
                        await bound.CancelAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogReleaseFailed(ex);
                }
            }
        }
    }

    /// <summary>Waits for every pending window to its real deadline; past <see cref="CleanupBound"/> one warning lists the keys still pending, and the wait goes on.</summary>
    /// <remarks>
    /// A pending key's final announcement must come after its window: a peer that refills the old value before then would miss a commit that lands late. A write
    /// of unknown outcome therefore costs a slower shutdown; a process killed during that wait falls into the crash case, where peers rely on their local cap.
    /// </remarks>
    public async Task SettlePendingAsync(IRouterParticipant[] participants)
    {
        var settled = Task.WhenAll(participants.Select(p => p.WhenPendingSettledAsync()));
        if (!await BoundedStop.CompletesAsync(settled, CleanupBound).ConfigureAwait(false))
        {
            var pending = participants.SelectMany(p => p.PendingKeys()).Select(p => $"{p.Cache}/{p.Key} until {p.Until:O}");
            LogPendingWait(string.Join(", ", pending));
        }

        await settled.ConfigureAwait(false);
    }

    private void ExitActive()
    {
        if (Interlocked.Decrement(ref _active) == 0 && IsStopped)
        {
            _operationsDrained.TrySetResult();
        }
    }

    private void LeaveMode(int mode)
    {
        if (Interlocked.Decrement(ref _calls[mode].Value) == 0 && Volatile.Read(ref _callsDrained[mode]) is { } drained)
        {
            drained.TrySetResult();
        }
    }

    private FlagState ReadFlag(bool notify = true)
    {
        if (BypassFlag is null)
        {
            return FlagState.Off;
        }

        bool on;
        try
        {
            on = BypassFlag();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FlagState.Failed;
        }

        var value = on ? 1 : 0;
        if (Volatile.Read(ref _flagSeen) != value && Interlocked.Exchange(ref _flagSeen, value) != value && notify)
        {
            Run(ReconcileInBackground);
        }

        return on ? FlagState.On : FlagState.Off;
    }

    private RouterModeReason CurrentReason()
    {
        if (Volatile.Read(ref _flagSeen) == 1)
        {
            return RouterModeReason.Flag;
        }

        return Volatile.Read(ref _requested) == 1 ? RouterModeReason.Requested : RouterModeReason.LibraryUnavailable;
    }

    private void BeginBypass()
    {
        Volatile.Write(ref _tierClosed, 1);
        Volatile.Write(ref _mode, BypassIndex);
        LogMode(RouterMode.BypassLibrary, CurrentReason());
    }

    private Task ReconcileInBackground(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken, deliver: false);

    /// <summary>Moves the router to the mode its settings ask for, one transition at a time; false when it could not get there.</summary>
    /// <param name="deliver">Whether to wait for the handlers of the notifications; background work never does, so a handler may stop the router.</param>
    private async Task<bool> ReconcileAsync(CancellationToken cancellationToken, bool deliver = true)
    {
        // Counted from here to the end of the transition, so a stop waits for it before it disposes the lock it holds.
        Interlocked.Increment(ref _transitions);
        try
        {
            ObjectDisposedException.ThrowIf(IsStopped, typeof(IRouter));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            await _transition.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                while (WantBypass != (Volatile.Read(ref _mode) == BypassIndex))
                {
                    if (WantBypass)
                    {
                        await EnterAsync().ConfigureAwait(false);
                    }
                    else if (!await LeaveAsync(linked.Token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _transition.Release();
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _transitions) == 0 && IsStopped)
            {
                _transitionsEnded.TrySetResult();
            }
        }

        // Handlers run with no lock held and outside the transition, so one may call back into the router.
        var delivery = DispatchNotifications();
        if (deliver)
        {
            await delivery.ConfigureAwait(false);
        }

        EnsureLibraryStarting();
        return WantBypass == (Volatile.Read(ref _mode) == BypassIndex);
    }
    /// <summary>Shared-tier hits stop and the tier is cleared before anything else; then calls started in library mode end.</summary>
    private async Task EnterAsync()
    {
        Volatile.Write(ref _tierClosed, 1);
        Volatile.Write(ref _mode, BypassIndex);
        ResetLocal();
        await DrainAsync(LibraryIndex, SourceTimeout, _stopping.Token).ConfigureAwait(false);
        var reason = CurrentReason();
        LogMode(RouterMode.BypassLibrary, reason);
        Enqueue(RouterMode.BypassLibrary, reason);
    }

    /// <summary>Waits for subscriptions and pending writes, then switches to library mode, drains bypass operations, and resets the local tier. Returns false if the initial wait exhausts the shared deadline.</summary>
    private async Task<bool> LeaveAsync(CancellationToken cancellationToken)
    {
        var begun = Clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(SourceTimeout, Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await WaitUntilAsync(() => Snapshot().All(participant => participant.IsSubscribed), linked.Token).ConfigureAwait(false);
            await Task.WhenAll(Snapshot().Select(participant => participant.WhenPendingSettledAsync())).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            LogLeaveTimedOut();
            RouterTelemetry.CountLeaveTimedOut();
            ScheduleRetry();
            return false;
        }

        if (WantBypass)
        {
            return true;
        }

        // From here the transition is committed and ends whatever the caller does.
        Volatile.Write(ref _mode, LibraryIndex);
        var remaining = SourceTimeout - Clock.GetElapsedTime(begun);
        await DrainAsync(BypassIndex, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, _stopping.Token).ConfigureAwait(false);
        ResetLocal();
        if (Volatile.Read(ref _mode) == LibraryIndex)
        {
            Volatile.Write(ref _tierClosed, 0);
        }

        LogMode(RouterMode.Library, RouterModeReason.Default);
        Enqueue(RouterMode.Library, RouterModeReason.Default);
        return true;
    }

    private void ScheduleRetry()
    {
        if (Interlocked.CompareExchange(ref _retryScheduled, 1, 0) == 0)
        {
            Run(async token =>
            {
                try
                {
                    await Task.Delay(SourceTimeout, Clock, token).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _retryScheduled, 0);
                }

                await ReconcileAsync(token, deliver: false).ConfigureAwait(false);
            });
        }
    }

    private void ResetLocal()
    {
        Tier.Clear();
        foreach (var participant in Snapshot())
        {
            participant.ResetLocal();
        }
    }

    private IRouterParticipant[] Snapshot()
    {
        lock (_participants)
        {
            return [.. _participants];
        }
    }

    private async Task DrainAsync(int mode, TimeSpan bound, CancellationToken cancellationToken)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _callsDrained[mode], drained);
        if (Volatile.Read(ref _calls[mode].Value) == 0)
        {
            drained.TrySetResult();
        }

        try
        {
            await drained.Task.WaitAsync(bound, Clock, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A call that outlives the bound is left behind; its marks are stale, so it cannot fill the tier.
            LogDrainTimedOut(mode == LibraryIndex ? "library" : "bypass");
        }
        catch (OperationCanceledException)
        {
            // The router is stopping.
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(SubscriptionPoll, Clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Enqueue(RouterMode mode, RouterModeReason reason)
    {
        RouterTelemetry.CountModeChange(mode, reason);
        _notifications.Enqueue((mode, reason));
    }

    /// <summary>One dispatcher at a time keeps the order, on a task of its own that a stop does not wait for; a handler that calls back in leaves its notification to the dispatcher already running.</summary>
    private Task DispatchNotifications() =>
        _notifications.IsEmpty || Interlocked.CompareExchange(ref _dispatching, 1, 0) != 0 ? Task.CompletedTask : Task.Run(DeliverNotifications, CancellationToken.None);

    private void DeliverNotifications()
    {
        do
        {
            try
            {
                while (_notifications.TryDequeue(out var change))
                {
                    try
                    {
                        ModeChanged?.Invoke(change.Mode, change.Reason);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        LogModeHandlerFailed(ex);
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _dispatching, 0);
            }
        }
        while (!_notifications.IsEmpty && Interlocked.CompareExchange(ref _dispatching, 1, 0) == 0);
    }
    private void EnsureLibraryStarting(bool immediate = false)
    {
        if (_libraryFactory is not null && !LibraryStarted && !IsStopped && Interlocked.CompareExchange(ref _startLoop, 1, 0) == 0)
        {
            Run(token => StartLoopAsync(immediate, token));
        }
    }

    private async Task StartLoopAsync(bool immediate, CancellationToken cancellationToken)
    {
        var delay = FirstStartRetry;
        var wait = !immediate;
        var failedBefore = false;
        try
        {
            while (!LibraryStarted && !cancellationToken.IsCancellationRequested)
            {
                if (wait)
                {
                    await Task.Delay(delay, Clock, cancellationToken).ConfigureAwait(false);
                }

                wait = true;

                // The bypass flag asks for no library at all, so the factory does not run while it is on.
                if (Volatile.Read(ref _flagSeen) == 1)
                {
                    continue;
                }

                var started = await TryStartLibraryAsync(cancellationToken).ConfigureAwait(false);
                _firstAttempt.TrySetResult();
                if (started)
                {
                    return;
                }

                if (failedBefore)
                {
                    delay = delay * 2 > MaxStartRetry ? MaxStartRetry : delay * 2;
                }

                failedBefore = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The router is stopping.
        }
        finally
        {
            Volatile.Write(ref _startLoop, 0);
            _firstAttempt.TrySetResult();
        }
    }

    private async Task<bool> TryStartLibraryAsync(CancellationToken cancellationToken)
    {
        // On its own thread-pool task, so a factory that blocks or never returns holds nothing but itself.
        var attempt = Task.Run(() => _libraryFactory!(cancellationToken), CancellationToken.None);
        LibraryParts parts;
        try
        {
            parts = await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Fenced: a result that arrives after the stop is released, never used.
            _ = attempt.ContinueWith(
                done => done.IsCompletedSuccessfully ? StopHostedAsync(done.Result) : Task.FromResult(done.Exception),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RouterTelemetry.CountLibraryStartFailed();
            LogLibraryStartFailed(ex);
            return false;
        }

        try
        {
            AttachAll(parts);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await StopHostedAsync(parts).ConfigureAwait(false);
            RouterTelemetry.CountLibraryStartFailed();
            LogLibraryStartFailed(ex);
            return false;
        }

        Volatile.Write(ref _started, parts);
        Volatile.Write(ref _libraryDown, 0);
        LogLibraryStarted();
        Run(ReconcileInBackground);
        return true;
    }

    /// <summary>All caches or none: a failure part way gives back what the earlier ones took.</summary>
    private void AttachAll(LibraryParts parts)
    {
        var prepared = new List<IRouterParticipant.IAttachment>();
        try
        {
            foreach (var participant in Snapshot())
            {
                prepared.Add(participant.Prepare(parts));
            }
        }
        catch
        {
            foreach (var attachment in prepared)
            {
                attachment.Dispose();
            }

            throw;
        }

        foreach (var attachment in prepared)
        {
            attachment.Commit();
        }
    }

    private async Task StopHostedAsync(LibraryParts parts)
    {
        for (var i = parts.Started.Count - 1; i >= 0; i--)
        {
            try
            {
                using var limit = new CancellationTokenSource(CleanupBound, Clock);
                await BoundedAsync(parts.Started[i].StopAsync(limit.Token), "hosted service stop").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogReleaseFailed(ex);
            }
        }
    }

    /// <summary>A step that ignores its token is left running after the bound, counted and logged, and the next step goes ahead.</summary>
    private async Task BoundedAsync(Task step, string name)
    {
        if (!await BoundedStop.CompletesAsync(step, CleanupBound, Clock).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _unfinishedCleanup);
            RouterTelemetry.CountCleanupTimedOut();
            LogCleanupTimedOut(name);
        }
    }
    [LoggerMessage(Level = LogLevel.Warning, Message = "The router's dispose waits for keys whose write may still land: {Pending}.")]
    private partial void LogPendingWait(string pending);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router mode is {Mode} ({Reason}).")]
    private partial void LogMode(RouterMode mode, RouterModeReason reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The library stack could not start; every call stays on the source path and the start is retried.")]
    private partial void LogLibraryStartFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "The library stack started.")]
    private partial void LogLibraryStarted();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Calls started in {Mode} mode were still running after the source timeout; the mode change went ahead.")]
    private partial void LogDrainTimedOut(string mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The router stays in bypass: the library subscriptions or pending writes did not settle within the source timeout; it tries again.")]
    private partial void LogLeaveTimedOut();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the library stack failed on one step; the rest went ahead.")]
    private partial void LogReleaseFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup step {Step} did not finish in its bound and was left running.")]
    private partial void LogCleanupTimedOut(string step);

    [LoggerMessage(Level = LogLevel.Error, Message = "A ModeChanged handler threw.")]
    private partial void LogModeHandlerFailed(Exception exception);
}
