namespace UiPath.Caching.Telemetry;

public interface ITelemetryOperation
{
    void Start();
    void Stop();
    void Track(bool hit);

}
