namespace UiPath.Platform.Caching.Telemetry;

public interface ITelemetryOperation
{
    void Start();
    void Stop();
    void Track(bool hit);

}
