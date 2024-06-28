using System.Globalization;
using Microsoft.Extensions.Hosting;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast.Redis;

public class RedisStreamHealthMaintainer : IHostedService
{
    private const int MaxDimensions = 10;

    private readonly IRedisConnector _redis;
    private readonly RedisStreamsTopicOptions _streamOptions;
    private readonly RedisCacheOptions _redisOptions;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<RedisStreamHealthMaintainer> _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly RedisValue _sourceUri;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ICachingTelemetryProvider _telemetryProvider;
    private readonly SemaphoreSlim _semaphore;
    private readonly ISystemClock _clock;
    private readonly HashSet<string> _allowedDimensions = new(StringComparer.OrdinalIgnoreCase) { "name" };
    private string _streamsSearchPattern = string.Empty;
    private PeriodicTimer? _timer;
    private RedisKey _lockKey;
    private RedisKey _quarantineKeyPrefix;
 
    private Lazy<bool> _supportsXtrimMinId = new(() => false);

    public RedisStreamHealthMaintainer(
        IRedisConnector redis,
        ICachingTelemetryProvider telemetryProvider,
        IOptions<RedisStreamsTopicOptions> streamOptionsAccessor,
        IOptions<RedisCacheOptions> redisOptionsAccessor,
        IOptions<CacheOptions> cacheOptionsAccessor,
        ILogger<RedisStreamHealthMaintainer> logger)
    {
        _redis = redis;
        _telemetryProvider = telemetryProvider;
        _logger = logger;
        _streamOptions = streamOptionsAccessor.Value;
        _redisOptions = redisOptionsAccessor.Value;
        _cacheOptions = cacheOptionsAccessor.Value;
        _cancellationToken = _cancellationTokenSource.Token;
        _sourceUri = (_cacheOptions.SourceUri ?? CacheOptions.MachineUri).ToString();
        _clock = _redisOptions.Clock ?? new SystemClock();
        _semaphore = new SemaphoreSlim(1);
    }

    internal Task Task { get; private set; } = Task.CompletedTask;

    private IDatabase Database => _redis.Database;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Initialize();
        Task = Task.Run(Start, _cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    private async Task Start()
    {
        while (!_cancellationToken.IsCancellationRequested && await _timer!.WaitForNextTickAsync() && await _semaphore.WaitAsync(0))
        {
            await CheckStreamsAsync(_cancellationToken).ConfigureAwait(false);
        }
    }

    internal void Initialize()
    {
        _timer = new PeriodicTimer(_streamOptions.MaintainerCheckInterval);
        var keyFactory = _redisOptions.RedisKeyStrategyFactory ?? new DefaultRedisKeyStrategyFactory();
        var stringKeyStrategy = keyFactory.Create(_cacheOptions, RedisTypePrefixes.String);
        var hashKeyStrategy = keyFactory.Create(_cacheOptions, RedisTypePrefixes.Hash);
        if (string.IsNullOrWhiteSpace(_streamOptions.MaintainerSearchPattern))
        {
            var redisStreamKeyStrategy = _streamOptions.RedisStreamKeyStrategy ?? new PrefixStrategy(RedisTypePrefixes.Streams, _cacheOptions);
            _streamsSearchPattern = redisStreamKeyStrategy.GetRedisKey("*").ToString();
        }
        else
        {
            _streamsSearchPattern = _streamOptions.MaintainerSearchPattern;
        }

        _lockKey = stringKeyStrategy.GetRedisKey(string.Join(_cacheOptions.Separator, nameof(RedisStreamHealthMaintainer), "Lock2"));
        _quarantineKeyPrefix = hashKeyStrategy.GetRedisKey(string.Join(_cacheOptions.Separator, "caching", "meta", "stream")+"$");
        _supportsXtrimMinId = new Lazy<bool>(() => _redis.Version >= Version.Parse("6.2.0"));

        var ignoredDimensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var dim in _streamOptions.TrackedClientDimensions)
        {
            if (_allowedDimensions.Count >= MaxDimensions)
            {
                ignoredDimensions.Add(dim);
            }
            else
            {
                _allowedDimensions.Add(dim);
            }
        }

        if (ignoredDimensions.Count != 0)
        {
            _logger.LogWarning("Ignored client dimensions {Dimensions}", string.Join(", ", ignoredDimensions));
        }
    }

    internal async Task CheckStreamsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool ownsLock = false;
        try
        {
            ownsLock = await Database.LockTakeAsync(_lockKey, _sourceUri, _streamOptions.MaintainerCheckInterval, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (!ownsLock)
            {
                return;
            }

            var minOffset = _clock.UtcNow.Subtract(_streamOptions.MaintainerTrimInterval);
            var streams = await GetAllStreamsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var stream in streams)
            {
                await CheckStreamAsync(stream, minOffset, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis stream monitor");
            if (ownsLock)
            {
                try
                {
                    await Database.LockReleaseAsync(_lockKey, _sourceUri, CommandFlags.DemandMaster).ConfigureAwait(false);
                }
                catch
                {
                    // no op
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task CheckStreamAsync(StreamContext context, DateTimeOffset minOffset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        _ = transaction.HashSetAsync(context.QuarantineKey, [], CommandFlags.DemandMaster).ConfigureAwait(false);
        _ = transaction.KeyExpireAsync(context.QuarantineKey, _streamOptions.MaintainerQuarantineInterval.Multiply(10), CommandFlags.DemandMaster).ConfigureAwait(false);
        var success = await transaction.ExecuteAsync();
        if (!success)
        {
            return;
        }

        if (Array.TrueForAll(groupInfos, g => g.ConsumerCount == 0))
        {
            _logger.LogWarning("Stream {StreamKey} has no consumers", context.StreamKey);
        }

        foreach (var groupInfo in groupInfos)
        {
            await CheckStreamGroupAsync(context, groupInfo, cancellationToken).ConfigureAwait(false);
        }

        if (_supportsXtrimMinId.Value && TryParseDeliveredIdToDatetimeOffset(streamInfo.LastGeneratedId, out var dateTimeOffset) && dateTimeOffset > minOffset)
        {
            long minId = minOffset.ToUnixTimeMilliseconds();
            var result = await Database.ExecuteAsync("XTRIM", [context.StreamKey.ToString(), "MINID", minId], CommandFlags.DemandMaster).ConfigureAwait(false);
            var entriesDeleted = (long)result;
            _logger.LogWarning("Entries deleted: {Entries} in stream {Stream}. MinId: {MinId}", entriesDeleted, context.StreamKey, minId);
        }
    }

    private async Task CheckEmptyStreamAsync(StreamContext context, StreamInfo streamInfo)
    {
        var delete = true;
        if (TryParseDeliveredIdToDatetimeOffset(streamInfo.LastGeneratedId, out var offset) && offset >= _clock.UtcNow.Subtract(_streamOptions.MaintainerQuarantineInterval))
        {
            _logger.LogWarning("Stream {StreamKey} not deleted, last generated id at {Offset}", context.StreamKey, offset.Value.ToString("O"));
            delete = false;
        }

        if (delete)
        {
            _logger.LogWarning("Stream {StreamKey} deleted", context.StreamKey);
            await Database.ExecuteAsync("DEL", [context.StreamKey, context.QuarantineKey], CommandFlags.DemandMaster).ConfigureAwait(false);
        }
    }

    private async Task CheckStreamGroupAsync(StreamContext context, StreamGroupInfo groupInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TrackStreamGroup(context.StreamKey, groupInfo);

        if (groupInfo.ConsumerCount == 0)
        {
            await CheckEmptyStreamGroupAsync(context, groupInfo, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CheckStreamGroupWithConsumersAsync(context, groupInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckStreamGroupWithConsumersAsync(StreamContext context, StreamGroupInfo groupInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Database.HashDeleteAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
        if (_streamOptions.TrackStatistics)
        {
            var streamConsumerInfos = await Database.StreamConsumerInfoAsync(context.StreamKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
            foreach (var consumerInfo in streamConsumerInfos)
            {
                CheckConsumer(context.StreamKey, groupInfo, consumerInfo);
            }
        }
    }

    private async Task CheckEmptyStreamGroupAsync(StreamContext context, StreamGroupInfo groupInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await Database.HashGetAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.PreferReplica).ConfigureAwait(false);
        DateTimeOffset? quarantineDatetimeOffset = null;
        if (value.HasValue)
        {
            quarantineDatetimeOffset = DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result) ? result : null;
        }

        if (quarantineDatetimeOffset.HasValue)
        {
            if (quarantineDatetimeOffset.Value <= _clock.UtcNow.Subtract(_streamOptions.MaintainerQuarantineInterval))
            {
                var success = await Database.StreamDeleteConsumerGroupAsync(context.StreamKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
                if (success)
                {
                    await Database.HashDeleteAsync(context.QuarantineKey, groupInfo.Name, CommandFlags.DemandMaster).ConfigureAwait(false);
                    _logger.LogWarning("Consumer group {Group} from stream {Stream} deleted", groupInfo.Name, context.StreamKey);
                }
            }

        }
        else
        {
            var success = await Database.HashSetAsync(context.QuarantineKey, groupInfo.Name, _clock.UtcNow.ToString("O"), When.Always, CommandFlags.DemandMaster).ConfigureAwait(false);
            if (success)
            {
                _logger.LogWarning("Consumer group {Group} from stream {Stream} added in quarantine", groupInfo.Name, context.StreamKey);
            }
        }
    }

    private void CheckConsumer(RedisKey stream, StreamGroupInfo groupInfo, StreamConsumerInfo consumerInfo)
    {
        var props = new Dictionary<string, string>();
        AddProp(props, "Name", consumerInfo.Name);
        AddProp(props, "IdleTimeInMilliseconds", consumerInfo.IdleTimeInMilliseconds.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "PendingMessageCount", consumerInfo.PendingMessageCount.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "Group", groupInfo.Name);
        AddProp(props, "Stream", stream.ToString());
        _telemetryProvider.TrackMetric(Metrics.StreamConsumer, groupInfo.Lag.GetValueOrDefault(), props);
    }

    private async Task<List<StreamContext>> GetAllStreamsAsync(CancellationToken cancellationToken)
    {
        var ret = new List<StreamContext>();
        ulong pointer = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await Database.ExecuteAsync("SCAN", [pointer, "MATCH", _streamsSearchPattern, "COUNT", 100, "TYPE", "stream"], CommandFlags.DemandMaster).ConfigureAwait(false);
            (ulong tempPointer, List<RedisKey> keys)  = ParseStreamScan(result);
            foreach (var key in keys)
            {
               ret.Add(new StreamContext(key, string.Concat(_quarantineKeyPrefix, key)));
            }
            if (tempPointer == 0)
            {
                break;
            }
            pointer = tempPointer;
        }

        return ret;
    }

    private void TrackStream(RedisKey stream, StreamInfo streamInfo)
    {
        if (!_streamOptions.TrackStatistics)
        {
            return;
        }

        var props = new Dictionary<string, string>();
        AddProp(props, "Name", stream.ToString());
        AddProp(props, "Length", streamInfo.Length.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "LastGeneratedId", streamInfo.LastGeneratedId.ToString());
        AddProp(props, "ConsumerGroupCount", streamInfo.ConsumerGroupCount.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "RadixTreeKeys", streamInfo.RadixTreeKeys.ToString(CultureInfo.InvariantCulture));
        AddProp(props, "RadixTreeNodes", streamInfo.RadixTreeNodes.ToString(CultureInfo.InvariantCulture));
        _telemetryProvider.TrackMetric(Metrics.Stream, streamInfo.Length, props);
    }

    private void TrackStreamGroup(RedisKey stream, StreamGroupInfo groupInfo)
    {
        if (!_streamOptions.TrackStatistics)
        {
            return;
        }
        var props = new Dictionary<string, string>();
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
        _telemetryProvider.TrackMetric(Metrics.StreamGroup, groupInfo.Lag.GetValueOrDefault(), props);
    }

    private static bool TryParseDeliveredIdToDatetimeOffset(string? entryId, out DateTimeOffset? dateTimeOffset)
    {
        dateTimeOffset = null;
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        var parts = entryId.Split('-');
        var timestampPart = parts[0];
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
            return false ;
        }
    }

    private static void AddProp(Dictionary<string, string> dictionary, string key, string? value)
    {
        if (!dictionary.ContainsKey(key) && !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
        {
            dictionary.Add(key, value);
        }
    }

    private static (ulong pointer, List<RedisKey> keys) ParseStreamScan(RedisResult result)
    {
        if (result.IsNull || result.Length == 0)
        {
            return (0, Enumerable.Empty<RedisKey>().ToList());
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
                    lst.Add((RedisKey)key);
                }
            }
        }

        return (pointer, lst);
    }

    private sealed record StreamContext(RedisKey StreamKey, RedisKey QuarantineKey);
}
