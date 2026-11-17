namespace UiPath.Caching.Telemetry;

/// <summary>Implemented by a telemetry provider so the library can skip building telemetry nothing would record; a provider that does not implement it is treated as listening.</summary>
public interface ITelemetryListening
{
    /// <summary>True when any signal the provider records on its own instruments or source would be recorded; read on every operation, so it must be cheap, thread-safe and never cached.</summary>
    bool IsListening { get; }
}
