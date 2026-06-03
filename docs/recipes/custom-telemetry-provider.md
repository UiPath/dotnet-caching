# Custom `ICachingTelemetryProvider`

**What:** Implement `ICachingTelemetryProvider` to bridge cache events to a host platform telemetry surface (an internal `ITelemetryProvider`, a structured logging pipeline, a metrics bus).

**When to use:**
- Your service has its own platform telemetry surface and you want cache events on it.
- You're not on AppInsights (so `.AddTelemetry()` doesn't fit) and not on OpenTelemetry-for-Redis (which uses the multiplexer factory instead).
- You want to control what events get emitted and how (e.g. drop some, redact others, route by category).

## Code

```csharp
using UiPath.Platform.Caching.Telemetry;

public class HostTelemetryBridge(IHostTelemetry hostTelemetry) : ICachingTelemetryProvider
{
    public void TrackEvent(
        string eventName,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default)
    {
        if (!hostTelemetry.IsEnabled(eventName)) return;
        hostTelemetry.Emit(eventName, ToDict(properties));
    }

    public void TrackMetric(
        string name,
        double value,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default) =>
        hostTelemetry.EmitMetric(name, value, ToDict(properties));

    public void TrackException(
        Exception ex,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        hostTelemetry.EmitException(ex, ToDict(properties));

    public void TrackDependency(
        string type, string target, string name, string data,
        DateTimeOffset startTime, TimeSpan duration,
        string resultCode, bool success,
        ReadOnlySpan<KeyValuePair<string, string>> properties = default,
        ReadOnlySpan<KeyValuePair<string, double>> metrics = default) =>
        hostTelemetry.EmitDependency(name, data, startTime, duration, success, ToDict(properties));

    private static Dictionary<string, string>? ToDict(
        ReadOnlySpan<KeyValuePair<string, string>> tags)
    {
        if (tags.IsEmpty) return null;
        var dict = new Dictionary<string, string>(tags.Length);
        foreach (var kv in tags) dict[kv.Key] = kv.Value;
        return dict;
    }
}
```

Register the bridge **instead of** calling `.AddTelemetry()` on the caching builder:

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
// Note: no .AddTelemetry() — the bridge replaces it.
```

## Notes

`IHostTelemetry` is a placeholder for your service's actual telemetry interface — adapt the method names to your host's API.

The interface takes tag bags as `ReadOnlySpan<KeyValuePair>` so the hot path is allocation-free when telemetry is disabled. Materializing the span into a `Dictionary` is only paid when you actually forward the event. If your host telemetry can accept `KeyValuePair[]` or a span-shaped API directly, skip the dict materialization entirely.

The `eventName` parameter on `TrackEvent` is the library's event name (e.g. `cache.miss`, `cache.write`, `cache.distributedlock.unavailable`). Filter by name if you only want a subset of events forwarded.

The interface has default no-op implementations for every method, so you only need to override the ones you care about. The default bodies exist to guard against Castle.Proxies mock-gen issues with `ref struct` parameters — they are intentionally not virtual.

## When not to use

- You're on AppInsights and `.AddTelemetry()` does what you need — one extra line beats a 60-line bridge.
- You only want Redis-level instrumentation (commands, dependencies) and don't care about cache-semantic events. Use the OTel multiplexer-factory path instead — see [recipes/opentelemetry-multiplexer-factory.md](opentelemetry-multiplexer-factory.md).

## See also

- [how-to/telemetry-and-strategies.md](../how-to/telemetry-and-strategies.md)
- [reference/interfaces.md](../reference/interfaces.md)
