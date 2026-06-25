namespace UiPath.Caching.Telemetry;

public interface ITelemetryOperation
{
    void Start();
    void Stop();
    void Track(bool hit);

    void Track(bool hit, int keyCount);

    void TrackKeyReads((string Key, bool Hit)[] reads);
}
