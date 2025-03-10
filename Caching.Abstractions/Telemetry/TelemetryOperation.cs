using System.Diagnostics;

namespace UiPath.Platform.Caching.Telemetry;

public sealed class TelemetryOperation(string providerName, string callerMethod, Type cacheObjectType, ICachingTelemetryProvider telemetryProvider) : ITelemetryOperation
{
    private const string Prefix = "Caching.Stats.";
    private const string Hits = Prefix + "Hits.";
    private const string Misses = Prefix + "Misses.";
    private readonly Stopwatch _stopWatch = new();


    public void Start() =>
        _stopWatch.Start();

    public void Stop() =>
        _stopWatch.Stop();

    public void Track(bool hit)
    {
        string key = string.Join('.', providerName, callerMethod, cacheObjectType.Name);
        if (hit)
        {
            telemetryProvider.TrackMetric($"{Hits}{key}", _stopWatch.ElapsedMilliseconds);
        }
        else
        {
            telemetryProvider.TrackMetric($"{Misses}{key}", _stopWatch.ElapsedMilliseconds);
        }
    }
}
