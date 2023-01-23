namespace UiPath.Platform.Caching.Telemetry;

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
}
