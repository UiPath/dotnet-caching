using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using UiPath.Caching.Negotiation;
using UiPath.Caching.Redis;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Broadcast.Redis;

public partial class RedisStreamHealthMaintainer : IHostedService
{
    private const string StreamKeyTerminator = "$";

    private const string DestroyIfIdleScript = """
        local ok, consumers = pcall(redis.call, 'XINFO', 'CONSUMERS', KEYS[1], ARGV[1])
        if not ok or #consumers == 0 then return 0 end
        for _, consumer in ipairs(consumers) do
            for i = 1, #consumer, 2 do
                if consumer[i] == 'idle' and tonumber(consumer[i + 1]) < tonumber(ARGV[2]) then return 0 end
            end
        end
        return redis.call('XGROUP', 'DESTROY', KEYS[1], ARGV[1])
        """;

    // From 7.2 a consumer's idle time counts every read attempt, so a live consumer on a quiet stream is never idle for long.
    private static readonly Version IdleCountsAttempts = new(7, 2);

    private readonly IRedisConnector _redis;
    private readonly ConnectorKeyPrefix _connectorPrefix;
    private readonly RedisStreamsTopicOptions _streamOptions;
    private readonly RedisCacheOptions _redisOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<RedisStreamHealthMaintainer> _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly RedisValue _sourceUri;
    private readonly RedisValue _lockValue;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeProvider _clock;
    private string _streamsSearchPattern = string.Empty;
    private bool _searchPatternIsDerived;
    private PeriodicTimer? _timer;
    private RedisKey _lockKey;
    private IRedisKeyStrategy _hashKeyStrategy = default!;
    private string _quarantineMarker = string.Empty;

    private Lazy<bool> _supportsXtrimMinId = new(() => false);
    private NodeListStore? _nodeList;
    private Task<DepartedOwners?>? _departed;
    private bool _idleCountsAttempts;

    public RedisStreamHealthMaintainer(
        IRedisConnector redis,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<RedisStreamsTopicOptions> streamOptionsAccessor,
        IOptions<RedisCacheOptions> redisOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        ILogger<RedisStreamHealthMaintainer> logger,
        TimeProvider clock)
    {
        _redis = redis;
        _connectorPrefix = ConnectorKeyPrefix.For(redis);
        _telemetryProvider = telemetryProvider;
        _logger = logger;
        _streamOptions = streamOptionsAccessor.Value;
        _redisOptions = redisOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _cancellationToken = _cancellationTokenSource.Token;
        _sourceUri = (_cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString();

        // The token tells two maintainers on one host apart, which the source URI of a shared machine cannot.
        _lockValue = $"{_sourceUri}|{Guid.NewGuid():N}";
        _clock = clock;
        _semaphore = new SemaphoreSlim(1);
    }

    internal Task Task { get; private set; } = Task.CompletedTask;

    internal RedisKey LockKey => _lockKey;

    internal bool HoldsDepartedSnapshot => _departed is not null;

    private IDatabase Database => _redis.Database;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_cacheOptions.BroadcastEnabled)
        {
            return Task.CompletedTask;
        }
        Initialize();
        Task = Task.Run(Start, _cancellationToken);
        Task.Forget();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    internal void Initialize()
    {
        _timer = new PeriodicTimer(_streamOptions.MaintainerCheckInterval);
        var keyFactory = _redisOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory();
        var stringKeyStrategy = keyFactory.Create(_cacheOptions, RedisKeyspaces.String);
        var hashKeyStrategy = keyFactory.Create(_cacheOptions, RedisKeyspaces.Hash);
        if (string.IsNullOrWhiteSpace(_streamOptions.MaintainerSearchPattern))
        {
            var redisStreamKeyStrategy = _streamOptions.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisKeyspaces.Streams, _cacheOptions);
            _streamsSearchPattern = redisStreamKeyStrategy.GetRedisKey("*").ToString();
            _searchPatternIsDerived = true;
        }
        else
        {
            _streamsSearchPattern = _streamOptions.MaintainerSearchPattern;
        }

        _nodeList = new NodeListStore(_redis, NodeListStore.KeyFor(_cacheOptions, _redisOptions));
        _lockKey = stringKeyStrategy.GetRedisKey(EscapeBraces(string.Join(_cacheOptions.Separator, nameof(RedisStreamHealthMaintainer), "Lock2")));
        _hashKeyStrategy = hashKeyStrategy;
        _quarantineMarker = string.Join(_cacheOptions.Separator, "caching", "meta", "stream") + "$";
        _supportsXtrimMinId = new Lazy<bool>(() => _redis.Version >= Version.Parse("6.2.0"));
    }

    internal async Task CheckStreamsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!BroadcastCapability.For(_redis).AllowsStreamWork)
        {
            // No streams to look after on a server that refuses them, and the commands would only fail.
            return;
        }

        bool ownsLock = false;
        try
        {
            ownsLock = await Database.LockTakeAsync(_lockKey, _lockValue, _streamOptions.MaintainerCheckInterval, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (!ownsLock)
            {
                return;
            }

            var minOffset = _clock.GetUtcNow().Subtract(_streamOptions.MaintainerTrimInterval);

            // Every primary, connected or not: one whose version cannot be read now may be older, and then absent owners are kept.
            var primaries = Database.Multiplexer?.GetServers().Where(static server => !server.IsReplica).ToList() ?? [];
            _idleCountsAttempts = primaries.Count > 0 && primaries.TrueForAll(static server => server.IsConnected && server.Version >= IdleCountsAttempts);
            var streams = await GetAllStreamsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var stream in streams)
            {
                await CheckStreamAsync(stream, minOffset, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogRedisStreamMonitorError(ex);
            if (ownsLock)
            {
                try
                {
                    await Database.LockReleaseAsync(_lockKey, _lockValue, CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                catch
                {
                    // no op
                }
            }
        }
        finally
        {
            // Read at most once per pass, and only once a group with consumers asks; nothing is held between passes.
            _departed = null;
            _semaphore.Release();
        }
    }

    // Built as one whole key so the strategy renders it once: composing a rendered prefix instead left every
    // quarantine hash under one tag, and so on one slot. Sensitive casing preserves the stream key's own case,
    // and the terminator keeps CacheKey's trim -- which runs in both casings -- from collapsing two stream keys
    // that differ only by surrounding whitespace.
    internal RedisKey QuarantineKey(RedisKey streamKey) =>
        _hashKeyStrategy.GetRedisKey(new CacheKey(
            EscapeBraces(_quarantineMarker + streamKey.ToString()) + StreamKeyTerminator,
            CacheKeyCasing.Sensitive));

    private static string EscapeBraces(string streamKey) => KeyEscaping.EscapeBraces(streamKey);

    private static bool TryParseDeliveredIdToDatetimeOffset(string? entryId, out DateTimeOffset? dateTimeOffset)
    {
        dateTimeOffset = null;
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        ReadOnlySpan<char> span = entryId.AsSpan();
        int separatorIndex = span.IndexOf('-');
        ReadOnlySpan<char> timestampPart = separatorIndex < 0 ? span : span[..separatorIndex];

        try
        {
            if (long.TryParse(timestampPart, NumberStyles.None, CultureInfo.InvariantCulture, out long result) && result > 0)
            {
                dateTimeOffset = DateTimeOffset.FromUnixTimeMilliseconds(result);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCaughtUp(string? lastDeliveredId, RedisValue lastGeneratedId) =>
        TryParseEntryId(lastDeliveredId, out var delivered)
        && TryParseEntryId(lastGeneratedId, out var generated)
        && delivered.CompareTo(generated) >= 0;

    private static bool TryParseEntryId(string? entryId, out (ulong Milliseconds, ulong Sequence) id)
    {
        id = default;
        var span = entryId.AsSpan();
        var separator = span.IndexOf('-');
        return separator > 0
            && ulong.TryParse(span[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out id.Milliseconds)
            && ulong.TryParse(span[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out id.Sequence);
    }

    private static List<IServer> ConnectedPrimaries(IConnectionMultiplexer? multiplexer) =>
        multiplexer is null ? [] : [.. ClusterServers.Nodes(multiplexer.GetServers()).Where(static server => !server.IsReplica && server.IsConnected)];

    private static void AddProp(List<KeyValuePair<string, string>> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
        {
            properties.Add(new(key, value));
        }
    }

    private static (ulong pointer, List<RedisKey> keys) ParseStreamScan(RedisResult result, string keyPrefix)
    {
        if (result.IsNull || result.Length == 0)
        {
            return (0, []);
        }

        var pointer = Convert.ToUInt64(result[0].ToString(), CultureInfo.InvariantCulture);
        var lst = new List<RedisKey>();

        if (result.Length == 2 && result[1].Length > 0)
        {
            var arr = result[1];
            for (int i = 0; i < arr.Length; i++)
            {
                var key = arr[i];
                if (key != null && !key.IsNull)
                {
                    var keyString = key.ToString();

                    // Strip the connector's prefix: the keys go back through its database, which adds it again.
                    if (!string.IsNullOrEmpty(keyPrefix) && keyString.StartsWith(keyPrefix, StringComparison.Ordinal))
                    {
                        keyString = keyString[keyPrefix.Length..];
                    }

                    lst.Add((RedisKey)keyString);
                }
            }
        }

        return (pointer, lst);
    }

    private async Task Start()
    {
        // Disposing the timer in StopAsync is what ends this loop, without an OperationCanceledException.
        while (!_cancellationToken.IsCancellationRequested
            && await _timer!.WaitForNextTickAsync(CancellationToken.None)
            && await _semaphore.WaitAsync(0, CancellationToken.None))
        {
            await CheckStreamsAsync(_cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckStreamAsync(StreamContext context, DateTimeOffset minOffset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The key may have been deleted between SCAN and processing; skip if it no longer exists
        var exists = await Database.KeyExistsAsync(context.StreamKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        if (!exists)
        {
            _logger.LogInformation("Stream {StreamKey} no longer exists. Skipping.", context.StreamKey);
            return;
        }

        var streamInfo = await Database.StreamInfoAsync(context.StreamKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        TrackStream(context.StreamKey, streamInfo);
        if (streamInfo.ConsumerGroupCount == 0)
        {
            await CheckEmptyStreamAsync(context, streamInfo).ConfigureAwait(false);
        }
        else
        {
            await CheckStreamWithGroupsAsync(context, minOffset, streamInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckStreamWithGroupsAsync(StreamContext context, DateTimeOffset minOffset, StreamInfo streamInfo, CancellationToken cancellationToken)
    {
        var groupInfos = await Database.StreamGroupInfoAsync(context.StreamKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        var transaction = Database.CreateTransaction();
        transaction.HashSetAsync(context.QuarantineKey, [], CommandFlags.DemandMaster).Forget();
        transaction.KeyExpireAsync(context.QuarantineKey, _streamOptions.MaintainerQuarantineInterval.Multiply(10), CommandFlags.DemandMaster).Forget();
        var success = await transaction.ExecuteAsync();
        if (!success)
        {
            return;
        }

        if (Array.TrueForAll(groupInfos, g => g.ConsumerCount == 0))
        {
            LogStreamHasNoConsumers(context.StreamKey);
        }

        foreach (var groupInfo in groupInfos)
        {
            await CheckStreamGroupAsync(context, groupInfo, minOffset, streamInfo.LastGeneratedId, cancellationToken).ConfigureAwait(false);
        }

        if (_supportsXtrimMinId.Value && TryParseDeliveredIdToDatetimeOffset(streamInfo.LastGeneratedId, out var dateTimeOffset) && dateTimeOffset > minOffset)
        {
            long minId = minOffset.ToUnixTimeMilliseconds();
            try
            {
                var entriesDeleted = await Database.StreamTrimByMinIdAsync(context.StreamKey, minId, flags: CommandFlags.DemandMaster).ConfigureAwait(false);
                LogEntriesDeleted(entriesDeleted, context.StreamKey, minId);
            }
            catch (RedisServerException ex) when (RedisUtils.IsUnsupportedCommand(ex) || RedisUtils.IsDenied(ex) || ex.Message.Contains("syntax error", StringComparison.OrdinalIgnoreCase))
            {
                // The version is a hint: a server that reports 6.2 or later and still refuses MINID is trimmed by the group checks alone.
                _supportsXtrimMinId = new Lazy<bool>(() => false);
                LogXtrimMinIdUnavailable(ex, context.StreamKey);
            }
        }
    }

    private async Task CheckEmptyStreamAsync(StreamContext context, StreamInfo streamInfo)
    {
        var delete = true;
        if (TryParseDeliveredIdToDatetimeOffset(streamInfo.LastGeneratedId, out var offset) && offset >= _clock.GetUtcNow().Subtract(_streamOptions.MaintainerQuarantineInterval))
        {
            LogStreamNotDeleted(context.StreamKey, offset);
            delete = false;
        }

        if (delete)
        {
            LogStreamDeleted(context.StreamKey);
            // One key per command. A multi-key delete would need both on one Redis Cluster slot, which the
            // configured IRedisKeyStrategy and any connection key prefix are free to prevent. The quarantine
            // hash goes first: deleting the stream is what makes the pair undiscoverable by the next SCAN, so
            // failing after that would strand the hash, which may carry no TTL yet.
            await Database.KeyDeleteAsync(context.QuarantineKey, CommandFlags.DemandMaster).ConfigureAwait(false);
            await Database.KeyDeleteAsync(context.StreamKey, CommandFlags.DemandMaster).ConfigureAwait(false);
        }
    }

    private async Task CheckStreamGroupAsync(StreamContext context, StreamGroupInfo groupInfo, DateTimeOffset minOffset, RedisValue lastGeneratedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TrackStreamGroup(context.StreamKey, groupInfo);

        if (groupInfo.ConsumerCount == 0)
        {
            await CheckEmptyStreamGroupAsync(context, groupInfo, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CheckStreamGroupWithConsumersAsync(context, groupInfo, minOffset, lastGeneratedId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckStreamGroupWithConsumersAsync(StreamContext context, StreamGroupInfo groupInfo, DateTimeOffset minOffset, RedisValue lastGeneratedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A group at the head of a quiet stream has nothing left to read, however old its last delivery.
        if (!IsCaughtUp(groupInfo.LastDeliveredId, lastGeneratedId) &&
            TryParseDeliveredIdToDatetimeOffset(groupInfo.LastDeliveredId, out var lastDeliveredOffset) &&
            lastDeliveredOffset.HasValue &&
            lastDeliveredOffset.Value < minOffset)
        {
            _logger.LogWarning(
                "Consumer group {Group} from stream {Stream} has stale last-delivered-id. LastDeliveredId: {LastDeliveredId}, LastDeliveredDate: {LastDeliveredDate}, MinOffset: {MinOffset}",
                groupInfo.Name,
                context.StreamKey,
                groupInfo.LastDeliveredId,
                lastDeliveredOffset.Value.ToString("O"),
                minOffset.ToString("O"));

            var quarantinedAt = await GetQuarantinedAtAsync(context, groupInfo).ConfigureAwait(false);
            if (quarantinedAt is null)
            {
                await Database.HashSetAsync(context.QuarantineKey, groupInfo.Name, _clock.GetUtcNow().ToString("O"), When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
                _logger.LogWarning("Consumer group {Group} from stream {Stream} added to quarantine due to stale last-delivered-id", groupInfo.Name, context.StreamKey);
                return;
            }

            if (!QuarantineElapsed(quarantinedAt.Value))
            {
                return;
            }

            var deleted = await Database.StreamDeleteConsumerGroupAsync(context.StreamKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (deleted)
            {
                await Database.HashDeleteAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
                _logger.LogWarning("Consumer group {Group} from stream {Stream} deleted due to stale last-delivered-id (quarantined since {QuarantinedAt:O})", groupInfo.Name, context.StreamKey, quarantinedAt.Value);
            }

            return;
        }

        var (reason, fetched) = await DepartureAsync(context, groupInfo, cancellationToken).ConfigureAwait(false);
        if (reason is not null)
        {
            await DeleteDepartedAsync(context, groupInfo, reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Database.HashDeleteAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);

        if (_streamOptions.TrackStatistics)
        {
            var streamConsumerInfos = fetched ?? await Database.StreamConsumerInfoAsync(context.StreamKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
            foreach (var consumerInfo in streamConsumerInfos)
            {
                CheckConsumer(context.StreamKey, groupInfo, consumerInfo);
            }
        }
    }

    private async Task CheckEmptyStreamGroupAsync(StreamContext context, StreamGroupInfo groupInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var quarantinedAt = await GetQuarantinedAtAsync(context, groupInfo).ConfigureAwait(false);
        if (quarantinedAt is null)
        {
            var quarantined = await Database.HashSetAsync(context.QuarantineKey, groupInfo.Name, _clock.GetUtcNow().ToString("O"), When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (quarantined)
            {
                LogConsumerGroupQuarantined(groupInfo.Name, context.StreamKey);
            }

            return;
        }

        if (!QuarantineElapsed(quarantinedAt.Value))
        {
            return;
        }

        var deleted = await Database.StreamDeleteConsumerGroupAsync(context.StreamKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
        if (deleted)
        {
            await Database.HashDeleteAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
            LogConsumerGroupDeleted(groupInfo.Name, context.StreamKey);
        }
    }

    // A record means the wait has started, not that it is over: the lock expires on MaintainerCheckInterval
    // rather than on the pass finishing, so two overlapping passes could write and delete in the same cycle.
    private async Task<DateTimeOffset?> GetQuarantinedAtAsync(StreamContext context, StreamGroupInfo groupInfo)
    {
        // From the primary, like every write to this hash: a replica that has not caught up reports a
        // timestamp the primary already deleted, and the group is deleted without serving its wait.
        var value = await Database.HashGetAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
        return value.HasValue
            && DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var quarantinedAt)
                ? quarantinedAt
                : null;
    }

    /// <summary>Why the group's owner counts as departed, or null to keep it.</summary>
    /// <remarks>
    /// Kept: a fresh owner, one that cannot be judged, a node list that cannot be read, and every group before Redis 7.2, where a consumer's idle time
    /// ignores read attempts and so cannot protect a live owner that restarted. The consumers read on the way are returned for reuse.
    /// </remarks>
    private async Task<(string? Reason, StreamConsumerInfo[]? Consumers)> DepartureAsync(StreamContext context, StreamGroupInfo groupInfo, CancellationToken cancellationToken)
    {
        if (_nodeList is null || !_idleCountsAttempts || string.IsNullOrEmpty(groupInfo.Name))
        {
            return (null, null);
        }

        _departed ??= DepartedOwners.ReadAsync(_nodeList, _redisOptions.FormatNegotiation, _streamOptions.MaintainerQuarantineInterval, cancellationToken);
        return await _departed.ConfigureAwait(false) is { } owners ? await JudgeAsync(owners, context, groupInfo).ConfigureAwait(false) : (null, null);
    }

    private async Task<(string? Reason, StreamConsumerInfo[]? Consumers)> JudgeAsync(DepartedOwners owners, StreamContext context, StreamGroupInfo groupInfo)
    {
        var verdict = owners.Judge(groupInfo.Name);
        StreamConsumerInfo[]? consumers = null;
        if (verdict == DepartedOwners.Verdict.Unknown && owners.IsAbsent(groupInfo.Name) && owners.AnyFresh && _idleCountsAttempts)
        {
            try
            {
                consumers = await Database.StreamConsumerInfoAsync(context.StreamKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (RedisServerException ex) when (ex.Message.StartsWith("NOGROUP", StringComparison.OrdinalIgnoreCase))
            {
                // Deleted since the group list was read: nothing left to judge.
                return (null, null);
            }

            verdict = owners.JudgeAbsent(consumers, _idleCountsAttempts);
        }

        var reason = verdict switch
        {
            DepartedOwners.Verdict.Left => "left",
            DepartedOwners.Verdict.Stale => "stale",
            DepartedOwners.Verdict.AbsentIdle => "absent_idle",
            _ => null,
        };
        return (reason, consumers);
    }

    /// <summary>The quarantine every departed-group delete takes.</summary>
    /// <remarks>Recorded on the first pass that finds the owner departed, deleted once it has served its wait and still is.</remarks>
    private async Task DeleteDepartedAsync(StreamContext context, StreamGroupInfo groupInfo, string reason, CancellationToken cancellationToken)
    {
        var quarantinedAt = await GetQuarantinedAtAsync(context, groupInfo).ConfigureAwait(false);
        if (quarantinedAt is null)
        {
            await Database.HashSetAsync(context.QuarantineKey, groupInfo.Name, _clock.GetUtcNow().ToString("O"), When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
            LogDepartedGroupQuarantined(groupInfo.Name, context.StreamKey, reason);
            return;
        }

        if (!QuarantineElapsed(quarantinedAt.Value))
        {
            return;
        }

        if (!await StillDepartedAsync(context, groupInfo, reason, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (await DestroyIfIdleAsync(context.StreamKey, groupInfo.Name, reason).ConfigureAwait(false))
        {
            await Database.HashDeleteAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
            LogDepartedGroupDeleted(groupInfo.Name, context.StreamKey, reason);
            var props = new List<KeyValuePair<string, string>>(2);
            AddProp(props, "Stream", context.StreamKey.ToString());
            AddProp(props, "Reason", reason);
            _telemetryProvider.TrackMetric(Metrics.DepartedGroupDeleted, 1, CollectionsMarshal.AsSpan(props));
        }
    }

    /// <summary>Right before the delete, whether the group is still the delete's to take.</summary>
    /// <remarks>
    /// This maintainer still holds the lock (no overlapping pass), and a fresh read of the node list still finds the owner departed for the same reason, so
    /// an owner that came back with the same id is kept.
    /// </remarks>
    private async Task<bool> StillDepartedAsync(StreamContext context, StreamGroupInfo groupInfo, string reason, CancellationToken cancellationToken)
    {
        if (await Database.LockQueryAsync(_lockKey, CommandFlags.DemandMaster).ConfigureAwait(false) != _lockValue)
        {
            return false;
        }

        var owners = await DepartedOwners.ReadAsync(_nodeList!, _redisOptions.FormatNegotiation, _streamOptions.MaintainerQuarantineInterval, cancellationToken).ConfigureAwait(false);
        return owners is not null && (await JudgeAsync(owners, context, groupInfo).ConfigureAwait(false)).Reason == reason;
    }

    /// <summary>Deletes the group in one script on the stream's server unless a consumer of it read within the floor, or it has none.</summary>
    /// <remarks>An owner that restarted and read again keeps its group. Without scripts the check and the delete are two commands.</remarks>
    private async Task<bool> DestroyIfIdleAsync(RedisKey stream, string group, string reason)
    {
        var floor = (long)(reason == "absent_idle"
            ? DepartedOwners.IdleHorizon(_redisOptions.FormatNegotiation, _streamOptions.MaintainerQuarantineInterval)
            : _streamOptions.MaintainerQuarantineInterval).TotalMilliseconds;
        try
        {
            var result = await Database.ScriptEvaluateAsync(DestroyIfIdleScript, [stream], [group, floor], CommandFlags.DemandMaster).ConfigureAwait(false);
            return (long)result == 1;
        }
        catch (Exception ex) when (NodeListStore.IsScriptRefusal(ex))
        {
            try
            {
                var consumers = await Database.StreamConsumerInfoAsync(stream, group, CommandFlags.DemandMaster).ConfigureAwait(false);
                return consumers.Length > 0
                    && Array.TrueForAll(consumers, c => c.IdleTimeInMilliseconds >= floor)
                    && await Database.StreamDeleteConsumerGroupAsync(stream, group, CommandFlags.DemandMaster).ConfigureAwait(false);
            }
            catch (RedisServerException missing) when (missing.Message.StartsWith("NOGROUP", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
    }

    private bool QuarantineElapsed(DateTimeOffset quarantinedAt) =>
        quarantinedAt <= _clock.GetUtcNow().Subtract(_streamOptions.MaintainerQuarantineInterval);

    private void CheckConsumer(RedisKey stream, StreamGroupInfo groupInfo, StreamConsumerInfo consumerInfo)
    {
        var props = new List<KeyValuePair<string, string>>(5);
        AddProp(props, "Name", consumerInfo.Name);
        AddProp(props, "IdleTimeInMilliseconds", consumerInfo.IdleTimeInMilliseconds.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "PendingMessageCount", consumerInfo.PendingMessageCount.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "Group", groupInfo.Name);
        AddProp(props, "Stream", stream.ToString());
        _telemetryProvider.TrackMetric(Metrics.StreamConsumer, groupInfo.Lag.GetValueOrDefault(), CollectionsMarshal.AsSpan(props));
    }

    private async Task<List<StreamContext>> GetAllStreamsAsync(CancellationToken cancellationToken)
    {
        // A set: a slot in migration answers on both its source and its target primary.
        var discovered = new HashSet<RedisKey>();
        // SCAN answers with the keys as stored, so both the pattern and the parse need what the connector prepends.
        var keyPrefix = await _connectorPrefix.GetAsync(Database, _redisOptions.KeyPrefix ?? string.Empty, _logger, cancellationToken).ConfigureAwait(false);
        var primaries = _redis.GetPrimaries().ToList();
        if (primaries.Count == 0)
        {
            // A connector written outside this library inherits the empty default, yet its multiplexer knows every shard.
            primaries = ConnectedPrimaries(Database.Multiplexer);
        }

        if (primaries.Count == 0)
        {
            LogNoPrimariesToScan();
            await ScanStreamsAsync((command, args, flags) => Database.ExecuteAsync(command, args, flags), keyPrefix, discovered, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var database = Database.Database;

            // In parallel, so the pass costs the slowest shard rather than their sum and stays inside the lock.
            foreach (var keys in await Task.WhenAll(primaries.Select(primary => ScanPrimaryAsync(primary, database, keyPrefix, cancellationToken))).ConfigureAwait(false))
            {
                discovered.UnionWith(keys);
            }
        }

        var ret = new List<StreamContext>(discovered.Count);
        foreach (var key in discovered)
        {
            ret.Add(new StreamContext(key, QuarantineKey(key)));
        }

        return ret;
    }

    private async Task<HashSet<RedisKey>> ScanPrimaryAsync(IServer primary, int database, string keyPrefix, CancellationToken cancellationToken)
    {
        var keys = new HashSet<RedisKey>();
        try
        {
            // The database-scoped overload with the connector's database, as the routed SCAN used: the
            // three-argument one, like a null database, resolves to the configured default instead.
            await ScanStreamsAsync((command, args, flags) => primary.ExecuteAsync(database, command, args, flags), keyPrefix, keys, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPrimaryScanFailed(ex, primary.EndPoint);
        }

        return keys;
    }

    private async Task ScanStreamsAsync(
        Func<string, ICollection<object>, CommandFlags, Task<RedisResult>> executeAsync,
        string keyPrefix,
        HashSet<RedisKey> discovered,
        CancellationToken cancellationToken)
    {
        // An explicit MaintainerSearchPattern is taken as written; the derived one names keys before the prefix.
        var pattern = _searchPatternIsDerived ? keyPrefix + _streamsSearchPattern : _streamsSearchPattern;
        ulong pointer = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await executeAsync("SCAN", [pointer.ToString(CultureInfo.InvariantCulture), "MATCH", pattern, "COUNT", 100, "TYPE", "stream"], CommandFlags.DemandMaster).ConfigureAwait(false);

            (ulong tempPointer, List<RedisKey> keys) = ParseStreamScan(result, keyPrefix);
            discovered.UnionWith(keys);

            if (tempPointer == 0)
            {
                break;
            }

            pointer = tempPointer;
        }
    }

    private void TrackStream(RedisKey stream, StreamInfo streamInfo)
    {
        if (!_streamOptions.TrackStatistics)
        {
            return;
        }

        var props = new List<KeyValuePair<string, string>>(6);
        AddProp(props, "Name", stream.ToString());
        AddProp(props, "Length", streamInfo.Length.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "LastGeneratedId", streamInfo.LastGeneratedId.ToString());
        AddProp(props, "ConsumerGroupCount", streamInfo.ConsumerGroupCount.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "RadixTreeKeys", streamInfo.RadixTreeKeys.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "RadixTreeNodes", streamInfo.RadixTreeNodes.ToString(CultureInfo.InvariantCulture));
        _telemetryProvider.TrackMetric(Metrics.Stream, streamInfo.Length, CollectionsMarshal.AsSpan(props));
    }

    private void TrackStreamGroup(RedisKey stream, StreamGroupInfo groupInfo)
    {
        if (!_streamOptions.TrackStatistics)
        {
            return;
        }

        var props = new List<KeyValuePair<string, string>>(8);
        AddProp(props, "Name", groupInfo.Name);
        AddProp(props, "Lag", groupInfo.Lag.GetValueOrDefault(0).ToString(CultureInfo.InvariantCulture));
        AddProp(props, "LastDeliveredId", groupInfo.LastDeliveredId);
        AddProp(props, "PendingMessageCount", groupInfo.PendingMessageCount.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "EntriesRead", groupInfo.EntriesRead.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));
        AddProp(props, "ConsumerCount", groupInfo.ConsumerCount.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "Stream", stream.ToString());
        if (TryParseDeliveredIdToDatetimeOffset(groupInfo.LastDeliveredId, out var lastDeliveredDate) && lastDeliveredDate.HasValue)
        {
            AddProp(props, "LastDeliveredDate", lastDeliveredDate.Value.ToString("u"));
        }

        _telemetryProvider.TrackMetric(Metrics.StreamGroup, groupInfo.Lag.GetValueOrDefault(), CollectionsMarshal.AsSpan(props));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Redis stream monitor")]
    private partial void LogRedisStreamMonitorError(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stream discovery failed on primary {EndPoint}; its streams go unmaintained this cycle")]
    private partial void LogPrimaryScanFailed(Exception ex, EndPoint endPoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No primary could be enumerated for stream discovery; scanning only the routed server, which on a cluster reaches one shard")]
    private partial void LogNoPrimariesToScan();

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer group {Group} of stream {StreamKey} quarantined: its node has departed ({Reason})")]
    private partial void LogDepartedGroupQuarantined(string? group, RedisKey streamKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer group {Group} of stream {StreamKey} deleted: its node departed ({Reason}) and the quarantine has passed")]
    private partial void LogDepartedGroupDeleted(string? group, RedisKey streamKey, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {StreamKey} has no consumers")]
    private partial void LogStreamHasNoConsumers(RedisKey streamKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Entries deleted: {Entries} in stream {Stream}. MinId: {MinId}")]
    private partial void LogEntriesDeleted(long entries, RedisKey stream, long minId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {StreamKey} not deleted, last generated id at {Offset:O}")]
    private partial void LogStreamNotDeleted(RedisKey streamKey, DateTimeOffset? offset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {StreamKey} deleted")]
    private partial void LogStreamDeleted(RedisKey streamKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer group {Group} from stream {Stream} deleted")]
    private partial void LogConsumerGroupDeleted(string group, RedisKey stream);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer group {Group} from stream {Stream} added in quarantine")]
    private partial void LogConsumerGroupQuarantined(string group, RedisKey stream);

    [LoggerMessage(Level = LogLevel.Warning, Message = "XTRIM MINID was refused on stream {StreamKey}; trimming by minimum id is off for this maintainer")]
    private partial void LogXtrimMinIdUnavailable(Exception ex, RedisKey streamKey);

    private sealed record StreamContext(RedisKey StreamKey, RedisKey QuarantineKey);
}
