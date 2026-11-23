namespace UiPath.Caching.Broadcast.Redis;

/// <summary>Whether the server behind a connector runs Streams, found once per connector and shared by every topic and stack on it. The probe also checks that a deleted stream loses its consumer groups, which Kvrocks does not do.</summary>
internal sealed partial class BroadcastCapability
{
    private const string ProbeGroup = "g";
    private const string ProbeConsumer = "c";
    private static readonly TimeSpan ProbeKeyLifetime = TimeSpan.FromSeconds(60);

    private static readonly ConditionalWeakTable<IRedisConnector, BroadcastCapability> Instances = [];

    private readonly Lock _gate = new();
    private readonly IRedisConnector _connector;
    // The probe is the capability's, not its first caller's: it ends when it decides or when the connector is gone, whoever started it.
    private readonly TaskCompletionSource _decided = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StreamsProbeSettings? _settings;
    private Task? _probe;
    private bool _warnedSettings;
    private volatile StreamsState _state;
    private volatile string? _reason;
    private volatile bool _pubSubRefused;
    private volatile bool _shardedRefused;

    private BroadcastCapability(IRedisConnector connector) => _connector = connector;

    /// <summary>Raised on every change of state, on the thread that decided it.</summary>
    public event Action? Changed;

    public StreamsState State => _state;

    public string? Reason => _reason;

    /// <summary>Pub/Sub was refused as well, so there is nothing to fall back to.</summary>
    public bool PubSubIsRefused => _pubSubRefused;

    /// <summary>The server refused sharded Pub/Sub (SPUBLISH or SSUBSCRIBE): the Streams doorbells of this connector go over the literal channel.</summary>
    public bool ShardedPubSubIsRefused => _shardedRefused;

    /// <summary>Whether a probe has been started, so an Unknown state is a probe not done and not one never wanted.</summary>
    public bool Started => Volatile.Read(ref _probe) is not null;

    /// <summary>Streams work is not known to fail: the server runs it, or nothing has probed it.</summary>
    public bool AllowsStreamWork => _state == StreamsState.Supported || (_state == StreamsState.Unknown && !Started);

    internal TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    internal TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public static BroadcastCapability For(IRedisConnector connector) => Instances.GetValue(connector, static c => new BroadcastCapability(c));

    /// <summary>The settings a refusal is logged by, when no probe is started.</summary>
    public void Attach(StreamsProbeSettings settings)
    {
        lock (_gate)
        {
            _settings ??= settings;
        }
    }

    /// <summary>Starts the probe unless one runs or the state is decided, so the first caller probes for all of them. The settings of the first caller stand; a later caller's differing ones are warned about.</summary>
    public Task Start(StreamsProbeSettings settings)
    {
        lock (_gate)
        {
            var attached = _settings ??= settings;
            WarnIfDifferent(attached, settings);
            if (_state != StreamsState.Unknown)
            {
                return Task.CompletedTask;
            }

            if (_probe is null || _probe.IsCompleted)
            {
                // A key of its own per capability, so a probe of another node cannot be mistaken for this one's: under the streams prefix, for key-scoped ACLs.
                var probeKey = attached.ProbeKey.Append($"-{Guid.NewGuid():N}");
                // Nothing cancels the probe: it ends once the state is decided or the connector is disposed.
                _probe = Task.Run(() => ProbeAsync(attached, probeKey, CancellationToken.None), CancellationToken.None);
            }

            return _probe;
        }
    }

    /// <summary>Waits for the state to leave Unknown, at most <paramref name="timeout"/>; the state it is in then.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was cancelled.</exception>
    public async Task<StreamsState> WaitDecidedAsync(TimeSpan timeout, CancellationToken token = default)
    {
        if (_state == StreamsState.Unknown)
        {
            try
            {
                await _decided.Task.WaitAsync(timeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still unknown: the caller decides what that means.
            }
        }

        return _state;
    }

    /// <summary>A command of a topic was refused: the server does not run Streams, or this node may not.</summary>
    /// <returns>Whether the refusal switched the state to Unsupported.</returns>
    public bool Refused(Exception exception) => RedisUtils.IsServerRefusal(exception) && Latch(StreamsState.Unsupported, $"a Streams command was refused: {Describe(exception)}");

    /// <summary>Pub/Sub was refused too, so a topic on the fallback has nowhere to go.</summary>
    public void PubSubRefused(Exception exception)
    {
        if (!RedisUtils.IsServerRefusal(exception))
        {
            return;
        }

        StreamsProbeSettings? settings;
        lock (_gate)
        {
            // Once per connector, whichever topic and thread met it first.
            if (_pubSubRefused)
            {
                return;
            }

            _pubSubRefused = true;
            settings = _settings;
        }

        if (settings is not null)
        {
            LogPubSubRefused(settings.Logger, Describe(exception));
        }

        Changed?.Invoke();
    }

    /// <summary>SPUBLISH or SSUBSCRIBE was refused; the first one of the connector is logged, whichever topic met it.</summary>
    /// <returns>Whether it was a refusal, so the caller switches to the literal channel.</returns>
    public bool ShardedPubSubRefused(Exception exception, ILogger logger)
    {
        if (!RedisUtils.IsServerRefusal(exception))
        {
            return false;
        }

        bool first;
        lock (_gate)
        {
            first = !_shardedRefused;
            _shardedRefused = true;
        }

        if (first)
        {
            LogShardedRefused(logger, Describe(exception));
        }

        return true;
    }

    private static string Format(StreamsProbeSettings settings) => $"ProbeKey={settings.ProbeKey}, StreamsUnsupported={settings.Action}, UseStreamsOnQuirkServers={settings.UseOnQuirkServers}";

    private static string Describe(Exception exception) => exception.Message.Split('\n', 2)[0].Trim();

    private static void LogUnsupported(ILogger logger, StreamsUnsupportedAction action, string reason)
    {
        switch (action)
        {
            case StreamsUnsupportedAction.Fail:
                LogUnsupportedFail(logger, reason);
                break;
            case StreamsUnsupportedAction.Disable:
                LogUnsupportedDisabled(logger, reason);
                break;
            default:
                LogUnsupportedPubSub(logger, reason);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis Streams are not available ({Reason}); the Streams topics broadcast over Pub/Sub instead. Entries already on streams stay in place. Set Broadcast:RedisStreams:StreamsUnsupported to change this")]
    private static partial void LogUnsupportedPubSub(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis Streams are not available ({Reason}); broadcast over Streams is turned off, so this node neither sends nor receives cache invalidations. Set Broadcast:RedisStreams:StreamsUnsupported to change this")]
    private static partial void LogUnsupportedDisabled(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Redis Streams are not available ({Reason}) and Broadcast:RedisStreams:StreamsUnsupported is Fail; the Streams topics stay as they are and will keep failing")]
    private static partial void LogUnsupportedFail(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Redis Pub/Sub was refused as well as Streams ({Reason}); broadcast for the topics that fell back is turned off")]
    private static partial void LogPubSubRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sharded Pub/Sub was refused by the server ({Reason}); the Redis Streams notify doorbells use plain Pub/Sub channels instead. Set Broadcast:RedisStreams:NotifyShardedPubSub to false to silence this")]
    private static partial void LogShardedRefused(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stacks sharing a Redis connection have different Redis Streams probe settings; the first stands ({Used}) and the later ones are ignored ({Ignored})")]
    private static partial void LogSettingsDiffer(ILogger logger, string used, string ignored);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The Redis Streams probe found a consumer group on its own key {Key}; it tries again")]
    private static partial void LogProbeGroupExists(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The Redis Streams probe key {Key} was not given an expiry; it is deleted after the probe")]
    private static partial void LogProbeKeyNotExpiring(ILogger logger, Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The Redis Streams probe could not tell; it tries again in {Delay}")]
    private static partial void LogProbeFailed(ILogger logger, Exception ex, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The Redis Streams probe key {Key} was not deleted; it holds an empty stream")]
    private static partial void LogProbeKeyNotDeleted(ILogger logger, Exception ex, string key);

    /// <summary>The reason a server that keeps the consumer groups of a deleted stream (Kvrocks) goes to Pub/Sub, found by reading a group after a DEL and an XADD; <c>null</c> for a server that answers NOGROUP, as Redis does, or refuses nothing here.</summary>
    private static async Task<string?> FindQuirkAsync(IDatabase database, RedisKey probeKey, CancellationToken stopToken)
    {
        try
        {
            await database.KeyDeleteAsync(probeKey, CommandFlags.DemandMaster).WaitAsync(stopToken).ConfigureAwait(false);
            await database.StreamAddAsync(probeKey, "f", "1", flags: CommandFlags.DemandMaster).WaitAsync(stopToken).ConfigureAwait(false);
            await database.StreamReadGroupAsync(probeKey, ProbeGroup, ProbeConsumer, StreamPosition.NewMessages, count: 1, flags: CommandFlags.DemandMaster).WaitAsync(stopToken).ConfigureAwait(false);
            return "the server keeps the consumer groups of a deleted stream, so lost events would go unreported";
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        catch (RedisServerException ex) when (RedisUtils.IsServerRefusal(ex))
        {
            return $"a Streams command was refused: {Describe(ex)}";
        }
    }

    private static async Task DeleteProbeKeyAsync(IDatabase database, RedisKey probeKey, StreamsProbeSettings settings)
    {
        try
        {
            await database.KeyDeleteAsync(probeKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProbeKeyNotDeleted(settings.Logger, ex, probeKey.ToString());
        }
    }

    // So a node that dies mid-probe leaves nothing behind; the server that cannot do it is still probed.
    private static async Task ExpireProbeKeyAsync(IDatabase database, RedisKey probeKey, StreamsProbeSettings settings)
    {
        try
        {
            await database.KeyExpireAsync(probeKey, ProbeKeyLifetime, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
        {
            LogProbeKeyNotExpiring(settings.Logger, ex, probeKey.ToString());
        }
    }

    private async Task ProbeAsync(StreamsProbeSettings settings, RedisKey probeKey, CancellationToken stopToken)
    {
        var delay = InitialRetryDelay;
        while (_state == StreamsState.Unknown && !stopToken.IsCancellationRequested)
        {
            try
            {
                if (await AttemptAsync(settings, probeKey, stopToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when ((ex is OperationCanceledException && stopToken.IsCancellationRequested) || ex is ObjectDisposedException)
            {
                // The connector is gone: nothing left to probe.
                return;
            }
            catch (Exception ex)
            {
                LogProbeFailed(settings.Logger, ex, delay);
            }

            try
            {
                await Task.Delay(delay, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
        }
    }

    /// <returns>Whether the probe decided.</returns>
    private async Task<bool> AttemptAsync(StreamsProbeSettings settings, RedisKey probeKey, CancellationToken stopToken)
    {
        await _connector.ConnectAsync(stopToken).ConfigureAwait(false);
        var database = _connector.Database;
        try
        {
            await database.StreamCreateConsumerGroupAsync(probeKey, ProbeGroup, StreamPosition.NewMessages, createStream: true, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (RedisUtils.IsGroupExists(ex))
        {
            // The key is this capability's own, so a group on it says nothing about the server's quirks: not decided.
            LogProbeGroupExists(settings.Logger, probeKey.ToString());
            return false;
        }
        catch (Exception ex) when (RedisUtils.IsServerRefusal(ex))
        {
            Latch(StreamsState.Unsupported, $"XGROUP CREATE was refused: {Describe(ex)}");
            return true;
        }

        string? quirk = null;
        try
        {
            await ExpireProbeKeyAsync(database, probeKey, settings).ConfigureAwait(false);
            if (!settings.UseOnQuirkServers)
            {
                quirk = await FindQuirkAsync(database, probeKey, stopToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await DeleteProbeKeyAsync(database, probeKey, settings).ConfigureAwait(false);
        }

        Latch(quirk is null ? StreamsState.Supported : StreamsState.Unsupported, quirk);
        return true;
    }

    private void WarnIfDifferent(StreamsProbeSettings used, StreamsProbeSettings requested)
    {
        if (_warnedSettings || ReferenceEquals(used, requested) || (used.ProbeKey == requested.ProbeKey && used.Action == requested.Action && used.UseOnQuirkServers == requested.UseOnQuirkServers))
        {
            return;
        }

        _warnedSettings = true;
        LogSettingsDiffer(requested.Logger, Format(used), Format(requested));
    }

    private bool Latch(StreamsState state, string? reason)
    {
        StreamsProbeSettings? settings;
        lock (_gate)
        {
            // Unsupported is final; Supported stands until a refusal says otherwise.
            if (_state == StreamsState.Unsupported || _state == state)
            {
                return false;
            }

            _state = state;
            _reason = reason;
            settings = _settings;
        }

        if (state == StreamsState.Unsupported && settings is not null)
        {
            LogUnsupported(settings.Logger, settings.Action, reason ?? string.Empty);
        }

        _decided.TrySetResult();
        Changed?.Invoke();
        return true;
    }
}
