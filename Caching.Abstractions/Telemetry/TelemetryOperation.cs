using System.Diagnostics;
using System.Globalization;

namespace UiPath.Platform.Caching.Telemetry;

public sealed class TelemetryOperation(Type callerType, string callerMethod, Type? cacheObjectType, ICachingTelemetryProvider telemetryProvider) : ITelemetryOperation
{
    private const string HitKey = "Hit";
    private const string LatencyKey = "Latency";
    private const string CallerKey = "Caller";
    private const string CacheObjectTypeKey = "CacheObjectType";
    private readonly Stopwatch _stopWatch = new Stopwatch();

    public void Start() =>
        _stopWatch.Start();

    public void Stop() =>
        _stopWatch.Stop();

    public void Track(bool hit)
    {
        var props = new Dictionary<string, string>(3) {
            { CallerKey, callerMethod },
            { HitKey, hit.ToString(CultureInfo.InvariantCulture) }
        };
        if (cacheObjectType != null)
        {
            props.Add(CacheObjectTypeKey, cacheObjectType.Name);
        }

        telemetryProvider.TrackEvent(callerType.Name,
            properties: props,
            metrics: new Dictionary<string, double>(1) { { LatencyKey, _stopWatch.ElapsedMilliseconds } });
    }
}
