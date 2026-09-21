using System.Globalization;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis.Maintenance;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Redis;

[ExcludeFromCodeCoverage(Justification = "Wires up StackExchange.Redis ServerMaintenanceEvent — exercised only by real Azure Cache for Redis planned-maintenance notifications.")]
public sealed class RedisPlannedMaintenance : IRedisPlannedMaintenance, IHostedService
{
    /// <summary>How long a notification stays recognisable as one already recorded, and so what bounds the set.</summary>
    private static readonly TimeSpan SeenRetention = TimeSpan.FromSeconds(30);

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
    private readonly Queue<(string Raw, long At)> _seen = new();
    private readonly HashSet<string> _seenIdentities = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
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
        get => Interlocked.Read(ref _maintenanceInProgress) == 1;
        set => Interlocked.Exchange(ref _maintenanceInProgress, value ? 1 : 0);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Via the connector, so one subscription survives a ForceReconnect; the multiplexer's own would not.
        _redisConnector.ServerMaintenance += OnServerMaintenance;
        _ = Task.Run(() => InitializeAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
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
        await RedisConnectionConfigurators.ApplyAsync(configuration, _configurators, cancellationToken).ConfigureAwait(false);

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
        if (!TryClaim(e, out var claim))
        {
            return;
        }

        var recorded = false;
        try
        {
            Record(e);
            recorded = true;
        }
        finally
        {
            Settle(claim, recorded);
        }
    }

    private void Record(ServerMaintenanceEvent e)
    {
#pragma warning disable SER010 // Server-native maintenance notifications are for evaluation purposes only
        switch (e)
        {
            case AzureMaintenanceEvent azureEvent:
                OnAzureMaintenance(azureEvent);
                break;
            case PushMaintenanceEvent pushEvent:
                OnPushMaintenance(pushEvent);
                break;
            default:
                // Recording the base properties keeps a source we do not model visible, rather than silent.
                _telemetryProvider.TrackEvent(
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

        _telemetryProvider.TrackEvent(
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
    private void OnPushMaintenance(PushMaintenanceEvent pushEvent)
    {
        // Recorded, not acted on: the client handles the handoff itself, and probing force-reconnects on a
        // failed write, which would fight it.
        _telemetryProvider.TrackEvent(
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
            InProgress = false;
            return;
        }

        tokenSource.CancelAfter(_probingTime);
        var token = tokenSource.Token;

        // Never skip the delegate: its finally disposes the linked source and clears InProgress.
        _ = Task.Run(() => ProbeUntilCancelledAsync(tokenSource, token), CancellationToken.None);
    }

    private async Task ProbeUntilCancelledAsync(CancellationTokenSource tokenSource, CancellationToken token)
    {
        var started = false;
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

                // Under the same lock that cleared the guard: outside it, a shutdown landing between the two
                // would let this worker announce an interval that starts after the service stopped. A refused
                // announcement must not end the run -- probing is what it is for -- and leaves started false,
                // so there is no end to announce either.
                started = TryAnnounce("Redis.MaintenanceStarted");
            }

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
            // Cleared first: left set, the CompareExchange guard would refuse every later probe run.
            InProgress = false;
            tokenSource.Dispose();
            if (started)
            {
                _ = TryAnnounce("Redis.MaintenanceEnded");
            }
        }
    }

    /// <summary>One probe and the wait after it; false once the run should stop.</summary>
    private async Task<bool> ProbeOnceAsync(CancellationToken token)
    {
        try
        {
            var probeTask = _redisConnector.Database.StringSetAsync("probeRedis_" + Environment.MachineName, DateTime.UtcNow.ToString(CultureInfo.InvariantCulture), expiry: TimeSpan.FromDays(1));

            await probeTask.WaitAsync(_hangingTime, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Reporting must not cost the reconnect: that call is the whole point of probing.
            _telemetryProvider.TryTrackException(ex);
            _redisConnector.ForceReconnect();
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
}
