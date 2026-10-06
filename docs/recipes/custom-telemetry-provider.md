# Custom `ICachingTelemetryProvider`

**What:** Implement `ICachingTelemetryProvider` to bridge cache events to a host platform telemetry surface (an internal `ITelemetryProvider`, a structured logging pipeline, a metrics bus).

**When to use:**
- Your service has its own telemetry surface and you want cache events on it.
- The built-in OpenTelemetry adapter (`.AddOpenTelemetry()`) doesn't fit, and you want more than the Redis-command spans the OTel multiplexer factory provides.
- You want to control what events get emitted and how (e.g. drop some, redact others, route by category).

## Code

```csharp
using UiPath.Caching.Telemetry;

public class HostTelemetryBridge(IHostTelemetry hostTelemetry) : ICachingTelemetryProvider
{
    public void TrackEvent(
        string eventName,
        TelemetryTags<string> properties = default,
        TelemetryTags<double> metrics = default)
    {
        if (!hostTelemetry.IsEnabled(eventName)) return;
        hostTelemetry.Emit(eventName, ToDict(properties));
    }

    public void TrackMetric(
        string name,
        double value,
        TelemetryTags<string> properties = default) =>
        hostTelemetry.EmitMetric(name, value, ToDict(properties));

    public void TrackException(
        Exception ex,
        TelemetryTags<string> properties = default,
        TelemetryTags<double> metrics = default) =>
        hostTelemetry.EmitException(ex, ToDict(properties));

    public void TrackDependency(
        string type, string target, string name, string data,
        DateTimeOffset startTime, TimeSpan duration,
        string resultCode, bool success,
        TelemetryTags<string> properties = default,
        TelemetryTags<double> metrics = default) =>
        hostTelemetry.EmitDependency(name, data, startTime, duration, success, ToDict(properties));

    private static Dictionary<string, string>? ToDict(
        TelemetryTags<string> tags)
    {
        if (tags.IsEmpty) return null;
        var dict = new Dictionary<string, string>(tags.Count);
        foreach (var kv in tags) dict[kv.Key] = kv.Value;
        return dict;
    }
}
```

Register the bridge **instead of** calling `.AddOpenTelemetry()` on the caching builder:

```csharp
services.AddSingleton<ICachingTelemetryProvider, HostTelemetryBridge>();

services.AddCaching(
    configuration.GetSection("Caching"),
    builder => builder
        .AddRedisConnection()
        .AddBroadcast()
        .AddRedis()
        .AddInMemoryRedis()
        .AddMemory()
        .AddResilienceStrategies()
        .AddCloudEvents());
// Note: no .AddOpenTelemetry() — the bridge replaces it.
```

## Notes

`IHostTelemetry` is a placeholder for your service's actual telemetry interface — adapt the method names to your host's API.

The interface takes tag bags as `TelemetryTags<T>`, a struct that holds up to nine pairs inline, so the hot path is allocation-free when telemetry is disabled. Materializing the tags into a `Dictionary` is only paid when you actually forward the event. If your host telemetry can enumerate `KeyValuePair` pairs directly, skip the dict materialization entirely.

The `eventName` parameter on `TrackEvent` is the library's event name (e.g. `cache.miss`, `cache.write`, `cache.distributedlock.unavailable`). Filter by name if you only want a subset of events forwarded.

Implement all four methods; a method you do not forward gets an empty body. The interface has no default bodies, so Moq and NSubstitute can mock it, and a test can match the tags it receives, `[new("key", "value")]` included, because `TelemetryTags<T>` compares by value.

## When not to use

- The built-in OpenTelemetry adapter `.AddOpenTelemetry()` does what you need — one extra line beats a 60-line bridge.
- You only want Redis-level instrumentation (commands, dependencies) and don't care about cache-semantic events. Use the OTel multiplexer-factory path instead — see [recipes/opentelemetry-multiplexer-factory.md](opentelemetry-multiplexer-factory.md).

## See also

- [how-to/telemetry-and-strategies.md](../how-to/telemetry-and-strategies.md)
- [reference/interfaces.md](../reference/interfaces.md)
