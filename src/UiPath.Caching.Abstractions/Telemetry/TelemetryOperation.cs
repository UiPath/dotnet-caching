using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace UiPath.Caching.Telemetry;

public sealed class TelemetryOperation(string providerName, string callerMethod, Type cacheObjectType, ICachingTelemetryProvider telemetryProvider) : ITelemetryOperation
{

    public const string DependencyType = "Redis";
    public const string OutcomeTag = "Outcome";
    public const string ProviderTag = "Provider";
    public const string MethodTag = "Method";
    public const string TypeTag = "Type";
    public const string KeysTag = "Keys";
    public const string BatchIdTag = "BatchId";
    private const string Prefix = "Caching.Stats.";
    private const string Hits = Prefix + "Hits.";
    private const string Misses = Prefix + "Misses.";
    private const string HitOutcome = "Hit";
    private const string MissOutcome = "Miss";

    private static readonly ConcurrentDictionary<(string Provider, string Method, string TypeName), (string Hit, string Miss)> MetricNames = new();

    private readonly (string Hit, string Miss) _metricNames = MetricNames.GetOrAdd((providerName, callerMethod, cacheObjectType.Name), static scope =>
    {
        var name = string.Join('.', scope.Provider, scope.Method, scope.TypeName);
        return (Hits + name, Misses + name);
    });

    private DateTimeOffset? _startedAt;
    private long _runningSince;
    private long _accumulated;

    private TimeSpan Elapsed =>
        Stopwatch.GetElapsedTime(0, _accumulated + (_runningSince == 0 ? 0 : Stopwatch.GetTimestamp() - _runningSince));

    public void Start()
    {
        _startedAt = DateTimeOffset.UtcNow;
        if (_runningSince == 0)
        {
            _runningSince = Stopwatch.GetTimestamp();
        }
    }

    public void Stop()
    {
        if (_runningSince != 0)
        {
            _accumulated += Stopwatch.GetTimestamp() - _runningSince;
            _runningSince = 0;
        }
    }

    public void Track(bool hit) =>
        Track(hit, 1);

    public void Track(bool hit, int keyCount) =>
        telemetryProvider.TrackMetric(hit ? _metricNames.Hit : _metricNames.Miss, Elapsed.TotalMilliseconds, [new(KeysTag, keyCount.ToString(CultureInfo.InvariantCulture))]);

    public void TrackKeyReads((string Key, bool Hit)[] reads)
    {
        var elapsed = Elapsed;
        var startTime = _startedAt ?? (DateTimeOffset.UtcNow - elapsed);
        var batchId = Guid.NewGuid().ToString();
        var hitProperties = Properties(true, batchId);
        var missProperties = Properties(false, batchId);
        foreach (var (key, hit) in reads)
        {
            telemetryProvider.TrackDependency(
                type: DependencyType,
                target: providerName,
                name: callerMethod,
                data: key,
                startTime: startTime,
                duration: elapsed,
                resultCode: hit ? HitOutcome : MissOutcome,
                success: true,
                properties: hit ? hitProperties : missProperties);
        }
    }

    private KeyValuePair<string, string>[] Properties(bool hit, string batchId) =>
    [
        new(OutcomeTag, hit ? HitOutcome : MissOutcome),
        new(ProviderTag, providerName),
        new(MethodTag, callerMethod),
        new(TypeTag, cacheObjectType.Name),
        new(BatchIdTag, batchId),
    ];
}
