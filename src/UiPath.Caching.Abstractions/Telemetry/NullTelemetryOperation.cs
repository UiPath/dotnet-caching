namespace UiPath.Caching.Telemetry;

[ExcludeFromCodeCoverage]
public class NullTelemetryOperation : ITelemetryOperation
{
    public static readonly NullTelemetryOperation Instance = new();

    public void Start()
    {
        // noop
    }

    public void Stop()
    {
        // noop
    }

    public void Track(bool hit)
    {
        // noop
    }

    public void Track(bool hit, int keyCount)
    {
        // noop
    }

    public void TrackKeyReads((string Key, bool Hit)[] reads)
    {
        // noop
    }
}
