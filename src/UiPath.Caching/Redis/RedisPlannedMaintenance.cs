using System.Globalization;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis.Maintenance;
using UiPath.Caching.Policies;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage(Justification = "Wires up StackExchange.Redis ServerMaintenanceEvent — exercised only by real Azure Cache for Redis planned-maintenance notifications.")]
public sealed class RedisPlannedMaintenance : IRedisPlannedMaintenance, IHostedService, IDisruptionState
{
    /// <summary>How long a notification stays recognisable as one already recorded, and so what bounds the set.</summary>
    private static readonly TimeSpan MinSeenRetention = TimeSpan.FromSeconds(30);
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    private static readonly ConfigurationOptions MaintenanceDefaults = new();
#pragma warning restore SER010

    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly IRedisConnector _redisConnector;
    private readonly IRedisConfigurationOptionsProvider _redisConfigurationOptionsProvider;
    private readonly IConnectionMultiplexerFactory _connectionMultiplexerFactory;
    private readonly IEnumerable<IRedisConnectionConfigurator>? _configurators;
    private readonly ILogger<RedisPlannedMaintenance> _logger;
    private readonly int _connectionRetryCount;
    private readonly TimeSpan _connectionRetryDelay;
    private readonly TimeSpan _probingTime = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _probeInterval = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _hangingTime = TimeSpan.FromSeconds(10);
    private readonly object _lock = new();
    private readonly object _stateLock = new();
    private readonly object _seenLock = new();
    private readonly object _cancelLock = new();

    // Taken first: a notice adopts bounds and updates its window as one step.
    private readonly object _noticeLock = new();
    private readonly Queue<(string Raw, long At)> _seen = new();
    private readonly HashSet<string> _seenIdentities = new(StringComparer.Ordinal);
    private readonly object _windowLock = new();
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    private readonly Dictionary<MaintenanceNotificationType, List<AnnouncedWindow>> _outstanding = [];

    // Per live family, so a replay cannot change it after its claim expires.
    private readonly Dictionary<MaintenanceNotificationType, HashSet<string>> _familyIdentities = [];
#pragma warning restore SER010
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private CancellationTokenSource? _windowTimer;
    private bool _reportedInProgress;
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    private TimeSpan _relaxedTimeout = MaintenanceDefaults.MaintenanceRelaxedTimeout;
    private TimeSpan _relaxedWindowMax = MaintenanceDefaults.MaintenanceRelaxedWindowMax;
    private TimeSpan _postEventRelaxed = MaintenanceDefaults.MaintenancePostEventRelaxedDuration;
    private long _seenRetentionTicks = RetentionTicks(MaintenanceDefaults.MaintenanceRelaxedWindowMax, MaintenanceDefaults.MaintenancePostEventRelaxedDuration);
    private long _relaxedTimeoutTicks = MaintenanceDefaults.MaintenanceRelaxedTimeout.Ticks;
#pragma warning restore SER010

    // Written under _windowLock, read without it on every Redis operation.
    private long _openUntil = long.MinValue;
    private string? _adoptedConfiguration;
    private long? _disconnectedSince;
    private IConnectionMultiplexer? _multiplexer;
    private volatile bool _disposed;
    private bool _cancellationDisposed;
    private int _stopped;
    private long _maintenanceInProgress;

    public RedisPlannedMaintenance(
        ICachingTelemetryProvider telemetryProvider,
        IRedisConnector redisConnector,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        ILogger<RedisPlannedMaintenance> logger,
        IOptions<RedisConnectionOptions> options,
        IEnumerable<IRedisConnectionConfigurator>? configurators = null)
        : this(telemetryProvider, redisConnector, redisConfigurationOptionsProvider, connectionMultiplexerFactory, logger, options, configurators, TimeProvider.System)
    {
    }

    public RedisPlannedMaintenance(
        ICachingTelemetryProvider telemetryProvider,
        IRedisConnector redisConnector,
        IRedisConfigurationOptionsProvider redisConfigurationOptionsProvider,
        IConnectionMultiplexerFactory connectionMultiplexerFactory,
        ILogger<RedisPlannedMaintenance> logger,
        IOptions<RedisConnectionOptions> options,
        IEnumerable<IRedisConnectionConfigurator>? configurators,
        TimeProvider clock)
    {
        _clock = clock;
        _telemetryProvider = telemetryProvider;
        _redisConnector = redisConnector;
        _redisConfigurationOptionsProvider = redisConfigurationOptionsProvider;
        _connectionMultiplexerFactory = connectionMultiplexerFactory;
        _configurators = configurators;
        _logger = logger;
        _connectionRetryCount = Math.Max(1, options.Value.PlannedMaintenanceConnectionRetryCount);
        var retryDelay = options.Value.PlannedMaintenanceConnectionRetryDelay;
        _connectionRetryDelay = retryDelay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : retryDelay;
    }

    public bool InProgress
    {
        get => Interlocked.Read(ref _maintenanceInProgress) == 1 || AnnouncedWindowOpen;
        set => Interlocked.Exchange(ref _maintenanceInProgress, value ? 1 : 0);
    }

    /// <summary>The relaxed timeout while an announced window is open.</summary>
    public TimeSpan? SuggestedTimeout => AnnouncedWindowOpen ? new TimeSpan(Volatile.Read(ref _relaxedTimeoutTicks)) : null;

    // A window plus its tail, published as one word so both bounds are read together.
    private TimeSpan SeenRetention => new(Volatile.Read(ref _seenRetentionTicks));

    private bool AnnouncedWindowOpen => _clock.GetTimestamp() < Volatile.Read(ref _openUntil);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Via the connector, so one subscription survives a ForceReconnect; the multiplexer's own would not.
        _redisConnector.ServerMaintenance += OnServerMaintenance;
        Task.Run(() => InitializeAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token).Forget();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopReacting();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        IConnectionMultiplexer? multiplexer;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            multiplexer = _multiplexer;
            _multiplexer = null;
        }

        // Dispose can be reached without StopAsync, so both paths share this rather than drifting apart.
        StopReacting(multiplexer);
        DisposeCancellation();

        if (multiplexer is not null)
        {
            TryDisposeMultiplexer(multiplexer);
        }
    }

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    private static long RetentionTicks(TimeSpan relaxedWindowMax, TimeSpan postEventRelaxed)
    {
        var horizon = relaxedWindowMax + postEventRelaxed;
        return (horizon > MinSeenRetention ? horizon : MinSeenRetention).Ticks;
    }

    private static MaintenanceNotificationType? StarterFor(MaintenanceNotificationType completion) => completion switch
    {
        MaintenanceNotificationType.Migrated => MaintenanceNotificationType.Migrating,
        MaintenanceNotificationType.FailedOver => MaintenanceNotificationType.FailingOver,
        MaintenanceNotificationType.SlotMigrated => MaintenanceNotificationType.SlotMigrating,
        _ => null,
    };
#pragma warning restore SER010

    /// <summary>Stops the service reacting further; both the stop and the dispose path run it.</summary>
    private void StopReacting(IConnectionMultiplexer? multiplexer = null)
    {
        _redisConnector.ServerMaintenance -= OnServerMaintenance;

        // Cancelled first, so _stopped is set before anything still in flight reaches a guard.
        Cancel();

        if (multiplexer is null)
        {
            lock (_lock)
            {
                multiplexer = _multiplexer;
            }
        }

        if (multiplexer is not null)
        {
            multiplexer.ServerMaintenanceEvent -= OnMaintenanceConnectionEvent;
        }

        CloseAllAnnouncedWindows();
    }

    /// <summary>One transition for both routes, which can overlap.</summary>
    private void ReportAggregateState()
    {
        lock (_windowLock)
        {
            var open = InProgress;

            // No start while stopping, but still close an open one.
            if (open && (_disposed || Volatile.Read(ref _stopped) == 1))
            {
                return;
            }

            if (open == _reportedInProgress)
            {
                return;
            }

            // Only once announced, or an end would pair with nothing.
            if (TryAnnounce(open ? "Redis.MaintenanceStarted" : "Redis.MaintenanceEnded"))
            {
                _reportedInProgress = open;
            }
        }
    }

    private void PublishSeenRetention() =>
        Volatile.Write(ref _seenRetentionTicks, RetentionTicks(_relaxedWindowMax, _postEventRelaxed));

    private void AdoptMaintenanceBounds(ConfigurationOptions configuration, string rendered)
    {
        lock (_windowLock)
        {
            // With the bounds, so the marker always names them.
            _adoptedConfiguration = rendered;
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
            _relaxedTimeout = configuration.MaintenanceRelaxedTimeout;
            _relaxedWindowMax = configuration.MaintenanceRelaxedWindowMax;
            _postEventRelaxed = configuration.MaintenancePostEventRelaxedDuration;
#pragma warning restore SER010
            Volatile.Write(ref _relaxedTimeoutTicks, _relaxedTimeout.Ticks);
            PublishSeenRetention();

            // Shortened bounds can move a deadline into the past.
            ReevaluateWindow();
        }
    }

    // The sending connection was built with the configurators, so its bounds are the real ones.
    private void AdoptBoundsOf(object? sender)
    {
        if (sender is not IConnectionMultiplexer multiplexer)
        {
            return;
        }

        try
        {
            var rendered = multiplexer.Configuration;
            if (string.IsNullOrWhiteSpace(rendered))
            {
                return;
            }

            lock (_windowLock)
            {
                if (string.Equals(rendered, _adoptedConfiguration, StringComparison.Ordinal))
                {
                    return;
                }
            }

            AdoptMaintenanceBounds(ConfigurationOptions.Parse(rendered), rendered);
        }
        catch (Exception ex)
        {
            // Keep the current bounds; the notice is still recorded.
            _telemetryProvider.TryTrackException(ex);
        }
    }

    private void Cancel()
    {
        bool first;
        lock (_stateLock)
        {
            first = Interlocked.Exchange(ref _stopped, 1) == 0;
        }

        if (!first)
        {
            return;
        }

        // Outside _stateLock, since cancellation callbacks run inline and one taking that lock would deadlock.
        // Under _cancelLock, since Dispose frees the source there: otherwise it could free it before this runs.
        lock (_cancelLock)
        {
            if (_cancellationDisposed)
            {
                return;
            }

            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (Exception ex)
            {
                // Runs the registrations inline, so it throws what a caller did; stopping is not optional.
                _telemetryProvider.TryTrackException(ex);
            }
        }
    }

    /// <summary>
    /// Cancels and frees the source as one step, so a concurrent <see cref="Cancel"/> cannot still be inside it.
    /// Cancelling here too covers the case where this path won: the loser returns without cancelling.
    /// </summary>
    private void DisposeCancellation()
    {
        lock (_cancelLock)
        {
            if (_cancellationDisposed)
            {
                return;
            }

            _cancellationDisposed = true;
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (Exception ex)
            {
                // Unreachable while Cancel guards its own: a second Cancel on a cancelled source runs nothing.
                _telemetryProvider.TryTrackException(ex);
            }
            finally
            {
                _cancellationTokenSource.Dispose();
            }
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await TryConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (attempt < _connectionRetryCount)
                {
                    _logger.LogWarning(ex, "Redis planned-maintenance subscription attempt {Attempt} failed; retrying in {Delay}.", attempt, _connectionRetryDelay);
                }
                else
                {
                    _logger.LogWarning(ex, "Redis planned-maintenance subscription failed after {Attempts} attempts; giving up.", _connectionRetryCount);
                    return;
                }
            }

            try
            {
                await Task.Delay(_connectionRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task TryConnectAsync(CancellationToken cancellationToken)
    {
        var configuration = _redisConfigurationOptionsProvider.GetConfiguration();
        await RedisConnectionConfigurators.ApplyAsync(configuration, _configurators, _redisConfigurationOptionsProvider, cancellationToken).ConfigureAwait(false);

        // Not adopted: this connection relaxes nothing.

        var multiplexer = await _connectionMultiplexerFactory.CreateAsync(configuration, cancellationToken).ConfigureAwait(false);

        bool stopped;
        lock (_lock)
        {
            // _stopped as well as _disposed: StopAsync can finish while CreateAsync is in flight, having
            // already taken its snapshot, so publishing here would leave a subscribed connection behind it.
            stopped = _disposed || Volatile.Read(ref _stopped) == 1;
            if (!stopped)
            {
                // Subscribed under the lock that publishes the field, so the two are never separately visible.
                multiplexer.ServerMaintenanceEvent += OnMaintenanceConnectionEvent;
                _multiplexer = multiplexer;
            }
        }

        if (stopped)
        {
            TryDisposeMultiplexer(multiplexer);
        }
    }

    private void TryDisposeMultiplexer(IConnectionMultiplexer multiplexer)
    {
        try
        {
            multiplexer.ServerMaintenanceEvent -= OnMaintenanceConnectionEvent;
            multiplexer.Dispose();
        }
        catch (Exception ex)
        {
            _telemetryProvider.TryTrackException(ex);
        }
    }

    // Push frames are ignored here: this connection carries no commands, so a MOVING on it names a replacement
    // for a connection nothing is using.
    private void OnMaintenanceConnectionEvent(object? sender, ServerMaintenanceEvent e)
    {
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        if (e is PushMaintenanceEvent)
        {
            return;
        }
#pragma warning restore SER010

        try
        {
            OnServerMaintenance(sender, e);
        }
        catch (Exception ex)
        {
            // Attached straight to the client, unlike the command route, so nothing else keeps throws off its dispatch.
            _telemetryProvider.TryTrackException(ex);
        }
    }

    // Either route can deliver a copy of the same notification -- Azure's is a broadcast, and a push frame is
    // replayed on reconnect, which the client collapses only within the multiplexer that received it. So the
    // once-only claim is made on the notification's own identity, not on which connection ought to have had it.
    // Claimed before the handler runs so a concurrent copy still collapses, committed only on success -- see Settle.
    private bool TryClaim(ServerMaintenanceEvent e, out string? claim)
    {
        // Not RawMessage: the client parses Azure's payload into properties and leaves that null. Sequences are
        // shared across types, so the type is part of a push frame's key. Nothing stamped per copy is in either.
        static string? IdentityOf(ServerMaintenanceEvent e) => e switch
        {
            // A payload the client could not parse leaves every field at its default, RawMessage included, so two
            // unrelated ones would share an identity. Recorded individually, like a frame with no sequence. The
            // type string is part of that: the client keeps one it does not recognise, and that alone identifies.
            AzureMaintenanceEvent { NotificationType: AzureNotificationType.Unknown, StartTimeUtc: null, IPAddress: null, SslPort: 0, NonSslPort: 0 } unparsed
                when unparsed.NotificationTypeString == nameof(AzureNotificationType.Unknown) => null,
            AzureMaintenanceEvent azureEvent => string.Create(
                CultureInfo.InvariantCulture,
                $"{azureEvent.NotificationTypeString}|{azureEvent.StartTimeUtc:O}|{azureEvent.IsReplica}|{azureEvent.IPAddress}|{azureEvent.SslPort}|{azureEvent.NonSslPort}"),
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
            // An unreadable sequence also surfaces as zero, and the client declines to collapse those -- but zero
            // is a legitimate sequence too, and only the description separates them. If that wording ever changes
            // these fall back to being keyed like any other, which is the milder way to be wrong.
            PushMaintenanceEvent { RawMessage: { } description }
                when description.Contains(" seq=?", StringComparison.Ordinal) => null,
            PushMaintenanceEvent pushEvent => string.Create(
                CultureInfo.InvariantCulture,
                $"{pushEvent.NotificationType}|{pushEvent.SequenceId}"),
#pragma warning restore SER010
            // Nothing else has an identity to key on: RawMessage carries no uniqueness contract, and the
            // fallback exists to keep an unknown source visible.
            _ => null,
        };

        // Nothing to key on, so nothing to claim: always handled, never collapsed.
        if (IdentityOf(e) is not { Length: > 0 } raw)
        {
            claim = null;
            return true;
        }

        lock (_seenLock)
        {
            // Timestamps rather than UtcNow, so a host clock correction neither holds entries past the retention
            // nor drops the protection; read under the lock, since expiring from the head assumes that order.
            var now = _clock.GetTimestamp();

            // Nothing is enqueued twice, so an entry leaving the queue is the last of its identity.
            while (_seen.Count > 0 && _clock.GetElapsedTime(_seen.Peek().At, now) > SeenRetention)
            {
                _seenIdentities.Remove(_seen.Dequeue().Raw);
            }

            if (!_seenIdentities.Add(raw))
            {
                claim = null;
                return false;
            }

            claim = raw;
            return true;
        }
    }

    // A handler that threw recorded nothing, so holding its claim would collapse later copies into a record that
    // does not exist. Timestamped on commit, not on claim, so the queue stays ordered when two routes race.
    // A copy arriving while the first is still in Record is lost if that one then fails: holding it back means
    // waiting on the client's dispatch thread, across a foreign telemetry call, for a retry that would fail too.
    private void Settle(string? claim, bool recorded)
    {
        if (claim is null)
        {
            return;
        }

        lock (_seenLock)
        {
            if (recorded)
            {
                _seen.Enqueue((claim, _clock.GetTimestamp()));
            }
            else
            {
                _seenIdentities.Remove(claim);
            }
        }
    }

    private void OnServerMaintenance(object? sender, ServerMaintenanceEvent e)
    {
        lock (_noticeLock)
        {
            // Claim first, so a copy from a retired connection cannot resize the window.
            if (!TryClaim(e, out var claim))
            {
                return;
            }

            AdoptBoundsOf(sender);

            var recorded = false;
            try
            {
                Record(e, claim);
                recorded = true;
            }
            finally
            {
                Settle(claim, recorded);
            }
        }
    }

    private void Record(ServerMaintenanceEvent e, string? identity)
    {
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        switch (e)
        {
            case AzureMaintenanceEvent azureEvent:
                OnAzureMaintenance(azureEvent);
                break;
            case PushMaintenanceEvent pushEvent:
                OnPushMaintenance(pushEvent, identity);
                break;
            default:
                // Recording the base properties keeps a source we do not model visible, rather than silent.
                _telemetryProvider.TryTrackEvent(
                    "Redis.Maintenance",
                    [
                        new("Source", e.GetType().Name),
                        new("ReceivedTimeUtc", e.ReceivedTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                        new("StartTimeUtc", e.StartTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                        new("RawMessage", e.RawMessage ?? string.Empty),
                    ]);
                break;
        }
#pragma warning restore SER010
    }

    private void OnAzureMaintenance(AzureMaintenanceEvent azureEvent)
    {
        // Azure Cache for Redis announces the node going away but hands nothing off, so the connection has to be
        // probed back into health.
        if (azureEvent.NotificationType == AzureNotificationType.NodeMaintenanceStarting)
        {
            StartConnectionProbing();
        }

        _telemetryProvider.TryTrackEvent(
            "Redis.Maintenance",
            [
                new("Source", nameof(AzureMaintenanceEvent)),
                new("IPAddress", azureEvent.IPAddress?.ToString() ?? string.Empty),
                new("NotificationTypeString", azureEvent.NotificationTypeString),
                new("SslPort", azureEvent.SslPort.ToString(CultureInfo.InvariantCulture)),
                new("ReceivedTimeUtc", azureEvent.ReceivedTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                new("StartTimeUtc", azureEvent.StartTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                new("IsReplica", azureEvent.IsReplica.ToString(CultureInfo.InvariantCulture)),
                new("RawMessage", azureEvent.RawMessage ?? string.Empty),
            ]);
    }

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    private void OnPushMaintenance(PushMaintenanceEvent pushEvent, string? identity)
    {
        // No probing: it force-reconnects, fighting the client's handoff.
        switch (pushEvent.NotificationType)
        {
            case MaintenanceNotificationType.Moving:
            case MaintenanceNotificationType.Migrating:
            case MaintenanceNotificationType.FailingOver:
            case MaintenanceNotificationType.SlotMigrating:
                OpenAnnouncedWindow(pushEvent.NotificationType, pushEvent.Time, identity);
                break;
            case MaintenanceNotificationType.Migrated:
            case MaintenanceNotificationType.FailedOver:
            case MaintenanceNotificationType.SlotMigrated:
                CloseAnnouncedWindow(pushEvent.NotificationType, identity);
                break;
            default:
                break;
        }

        _telemetryProvider.TryTrackEvent(
            "Redis.Maintenance",
            [
                new("Source", nameof(PushMaintenanceEvent)),
                new("NotificationTypeString", pushEvent.NotificationType.ToString()),
                new("SequenceId", pushEvent.SequenceId.ToString(CultureInfo.InvariantCulture)),
                new("EndPoint", pushEvent.EndPoint?.ToString() ?? string.Empty),
                new("NewEndPoint", pushEvent.NewEndPoint?.ToString() ?? string.Empty),
                new("SlotMigrations", pushEvent.SlotMigrations.Count.ToString(CultureInfo.InvariantCulture)),
                new("ReceivedTimeUtc", pushEvent.ReceivedTimeUtc.ToString("O", CultureInfo.InvariantCulture)),
                new("StartTimeUtc", pushEvent.StartTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty),
                new("RawMessage", pushEvent.RawMessage ?? string.Empty),
            ]);
    }
#pragma warning restore SER010

    /// <summary>Announces an interval boundary, reporting a sink that refuses rather than ending the run.</summary>
    private bool TryAnnounce(string eventName) => _telemetryProvider.TryTrackEvent(eventName);

#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
    // The announced duration is a hint; two-second windows have been seen.
    private TimeSpan RemainingOf(AnnouncedWindow window, long now) =>
        (window.Tail ? _postEventRelaxed : WindowFor(window.Announced)) - _clock.GetElapsedTime(window.At, now);

    private TimeSpan WindowFor(TimeSpan? announced)
    {
        var window = announced is { } duration && duration > TimeSpan.Zero ? duration : _relaxedTimeout;

        // Floor, then cap, so the cap always wins.
        if (window < _relaxedTimeout)
        {
            window = _relaxedTimeout;
        }

        return window > _relaxedWindowMax ? _relaxedWindowMax : window;
    }

    private void OpenAnnouncedWindow(MaintenanceNotificationType starter, TimeSpan? announced, string? identity)
    {
        lock (_windowLock)
        {
            // Under the lock StopAsync clears with.
            if (_disposed || Volatile.Read(ref _stopped) == 1 || !AdmitToFamily(starter, identity))
            {
                return;
            }

            // One entry per operation, so a shorter one cannot shorten a longer.
            var announcedWindow = new AnnouncedWindow(_clock.GetTimestamp(), announced, Tail: false);
            if (_outstanding.TryGetValue(starter, out var family))
            {
                family.Add(announcedWindow);
            }
            else
            {
                _outstanding[starter] = [announcedWindow];
            }

            ReevaluateWindow();
        }
    }

    private void CloseAllAnnouncedWindows()
    {
        lock (_windowLock)
        {
            _outstanding.Clear();
            _familyIdentities.Clear();
            ReevaluateWindow();
        }
    }

    private void CloseAnnouncedWindow(MaintenanceNotificationType completion, string? identity)
    {
        lock (_windowLock)
        {
            // Moving only lapses; other completions leave a tail, as the client does.
            if (StarterFor(completion) is { } starter && _outstanding.TryGetValue(starter, out var family) && family.Count > 0
                && AdmitToFamily(starter, identity))
            {
                // Which operation finished is unknowable, so release the one expiring first.
                var now = _clock.GetTimestamp();
                var running = family.Where(window => !window.Tail).ToList();
                if (running.Count > 0)
                {
                    var finished = running.OrderBy(window => RemainingOf(window, now)).First();
                    family.Remove(finished);

                    // Every live completion earns a tail, as the client relaxes on each.
                    if (_postEventRelaxed > TimeSpan.Zero
                        && RemainingOf(finished, now) > TimeSpan.Zero)
                    {
                        family.Add(new AnnouncedWindow(now, Announced: null, Tail: true));
                    }
                }

                if (family.Count == 0)
                {
                    RemoveFamily(starter);
                }
            }

            ReevaluateWindow();
        }
    }

    /// <summary>False when the family already applied this notice.</summary>
    private bool AdmitToFamily(MaintenanceNotificationType starter, string? identity)
    {
        if (identity is null)
        {
            return true;
        }

        if (!_familyIdentities.TryGetValue(starter, out var applied))
        {
            applied = new HashSet<string>(StringComparer.Ordinal);
            _familyIdentities[starter] = applied;
        }

        return applied.Add(identity);
    }

    private void RemoveFamily(MaintenanceNotificationType starter)
    {
        _outstanding.Remove(starter);
        _familyIdentities.Remove(starter);
    }

    private void ReevaluateWindow()
    {
        var now = _clock.GetTimestamp();
        foreach (var (starter, family) in _outstanding.ToList())
        {
            // Keep a lapsed operation while a live one could have its late completion taken.
            var live = family.Exists(window => !window.Tail && RemainingOf(window, now) > TimeSpan.Zero);
            family.RemoveAll(window => RemainingOf(window, now) <= TimeSpan.Zero && (window.Tail || !live));
            if (family.Count == 0)
            {
                RemoveFamily(starter);
            }
        }

        _windowTimer?.Cancel();
        _windowTimer?.Dispose();
        _windowTimer = null;

        var pending = _outstanding.Values.SelectMany(family => family).Select(window => RemainingOf(window, now)).Where(remaining => remaining > TimeSpan.Zero).ToList();

        // Before reporting, which reads it through InProgress.
        Volatile.Write(ref _openUntil, pending.Count == 0 ? long.MinValue : now + ToTimestampTicks(pending.Max()));
        ReportAggregateState();

        if (pending.Count == 0 || _disposed)
        {
            return;
        }

        // A completion can be lost, so the earliest deadline is re-armed here.
        var next = pending.Min();
        CancellationTokenSource timer;
        try
        {
            timer = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _windowTimer = timer;

        // Inline, so the timer is armed before this returns.
        CloseWhenElapsedAsync(next, timer).Forget();
    }
#pragma warning restore SER010

    private async Task CloseWhenElapsedAsync(TimeSpan next, CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(next, _clock, timer.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_windowLock)
        {
            ReevaluateWindow();
        }
    }

    private void StartConnectionProbing()
    {
        // Under the lock Cancel takes, so the check and the transition are one step: separately, a caller could
        // pass the check and then schedule a probe on a service that has since stopped.
        lock (_stateLock)
        {
            if (_disposed || Volatile.Read(ref _stopped) == 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _maintenanceInProgress, 1, 0) != 0)
            {
                return;
            }
        }

        CancellationTokenSource tokenSource;
        try
        {
            tokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            // Through the aggregate, so a started push window still gets its end.
            InProgress = false;
            ReportAggregateState();
            return;
        }

        tokenSource.CancelAfter(_probingTime);
        var token = tokenSource.Token;

        // Never skip the delegate: its finally disposes the linked source and clears InProgress.
        Task.Run(() => ProbeUntilCancelledAsync(tokenSource, token), CancellationToken.None).Forget();
    }

    private async Task ProbeUntilCancelledAsync(CancellationTokenSource tokenSource, CancellationToken token)
    {
        try
        {
            // Queued after the lock was released, so StopAsync can have completed in between; a worker that
            // lost that race announces nothing.
            lock (_stateLock)
            {
                if (_disposed || Volatile.Read(ref _stopped) == 1 || token.IsCancellationRequested)
                {
                    return;
                }
            }

            ReportAggregateState();

            while (!token.IsCancellationRequested)
            {
                if (!await ProbeOnceAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        finally
        {
            // Cleared first, or the CompareExchange guard refuses every later run.
            InProgress = false;
            ReportAggregateState();
            tokenSource.Dispose();
        }
    }

    /// <summary>One probe and the wait after it; false once the run should stop.</summary>
    private async Task<bool> ProbeOnceAsync(CancellationToken token)
    {
        Task? probeTask = null;
        try
        {
            probeTask = _redisConnector.Database.StringSetAsync("probeRedis_" + Environment.MachineName, DateTime.UtcNow.ToString(CultureInfo.InvariantCulture), expiry: TimeSpan.FromDays(1));

            await probeTask.WaitAsync(_hangingTime, token).ConfigureAwait(false);
            _disconnectedSince = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            probeTask?.Forget();
            return false;
        }
        catch (Exception ex)
        {
            // WaitAsync stops observing the probe.
            probeTask?.Forget();

            // Reporting must not cost the reconnect: that call is the whole point of probing.
            _telemetryProvider.TryTrackException(ex);

            // Fail-fast rejects while the client reconnects; rebuild only if that outlasts the hanging time.
            if (ex is not RedisConnectionException || _redisConnector.IsConnected || DisconnectedFor() >= _hangingTime)
            {
                _disconnectedSince = null;
                _redisConnector.ForceReconnect();
            }
        }

        try
        {
            await Task.Delay(_probeInterval, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    private TimeSpan DisconnectedFor()
    {
        var now = _clock.GetTimestamp();
        _disconnectedSince ??= now;
        return _clock.GetElapsedTime(_disconnectedSince.Value, now);
    }

    private long ToTimestampTicks(TimeSpan duration) =>
        (long)Math.Ceiling(duration.Ticks * (double)_clock.TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>What was announced and when, so later bounds can move its deadline.</summary>
    private readonly record struct AnnouncedWindow(long At, TimeSpan? Announced, bool Tail);
}
