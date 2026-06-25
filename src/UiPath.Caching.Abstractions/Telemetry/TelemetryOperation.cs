using System.Diagnostics;
using System.Globalization;

namespace UiPath.Caching.Telemetry;

public sealed class TelemetryOperation(string providerName, string callerMethod, Type cacheObjectType, ICachingTelemetryProvider telemetryProvider) : ITelemetryOperation
{
    private const string Prefix = "Caching.Stats.";
    private const string Hits = Prefix + "Hits.";
    private const string Misses = Prefix + "Misses.";

    public const string DependencyType = "Redis";
    public const string OutcomeTag = "Outcome";
    public const string ProviderTag = "Provider";
    public const string MethodTag = "Method";
    public const string TypeTag = "Type";
    public const string KeysTag = "Keys";
    public const string BatchIdTag = "BatchId";
    private const string HitOutcome = "Hit";
    private const string MissOutcome = "Miss";

    private readonly string _scope = string.Join('.', providerName, callerMethod, cacheObjectType.Name);
    private readonly Stopwatch _stopWatch = new();
    private DateTimeOffset? _startedAt;

    public void Start()
    {
        _startedAt = DateTimeOffset.UtcNow;
        _stopWatch.Start();
    }

    public void Stop() =>
        _stopWatch.Stop();

    public void Track(bool hit) =>
        Track(hit, 1);

    public void Track(bool hit, int keyCount) =>
        telemetryProvider.TrackMetric(MetricName(hit), _stopWatch.Elapsed.TotalMilliseconds, [new(KeysTag, keyCount.ToString(CultureInfo.InvariantCulture))]);

    public void TrackKeyReads((string Key, bool Hit)[] reads)
    {
        var elapsed = _stopWatch.Elapsed;
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

    private string MetricName(bool hit) =>
        $"{(hit ? Hits : Misses)}{_scope}";
}
