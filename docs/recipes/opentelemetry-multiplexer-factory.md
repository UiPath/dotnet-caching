# OpenTelemetry `IConnectionMultiplexerFactory`

**What:** A custom `IConnectionMultiplexerFactory` that registers each new `IConnectionMultiplexer` with the OpenTelemetry StackExchange.Redis instrumentation, so Redis commands show up in OTel spans.

**When to use:**
- You've chosen OpenTelemetry over AppInsights for telemetry.
- You want Redis commands to appear as OTel spans (alongside HTTP, DB, etc.).
- You need a single integration point for OTel Redis instrumentation that survives connection failures and reconnects.

## Code

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using StackExchange.Redis;
using UiPath.Platform.Caching.Redis;

public class OpenTelemetryConnectionMultiplexerFactory(
    IOptions<RedisConnectionOptions> redisOptions,
    IServiceProvider serviceProvider) : IConnectionMultiplexerFactory
{
    public IConnectionMultiplexer Create(ConfigurationOptions configuration)
    {
        var connection = redisOptions.Value.ConnectionFactory?.Invoke(configuration)
                         ?? ConnectionMultiplexer.Connect(configuration);
        serviceProvider.GetService<StackExchangeRedisInstrumentation>()
            ?.AddConnection(connection);
        return connection;
    }
}
```

Wire in code:

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddRedisInstrumentation(opt => opt.SetVerboseDatabaseStatements = true));

var section = builder.Configuration.GetSection("Caching");
builder.Services.AddCaching(
    section,
    b =>
    {
        b.Services.AddTransient<IConnectionMultiplexerFactory,
            OpenTelemetryConnectionMultiplexerFactory>();
        b.AddRedisConnection()
         .AddBroadcast()
         .AddRedis()
         .AddInMemoryRedis()
         .AddMemory()
         .AddResilienceStrategies()
         .AddCloudEvents();
        // No .AddTelemetry() — OTel owns Redis instrumentation.
    },
    options => section.Bind(options));   // bind root CacheOptions (AppShortName, DefaultCache, …)
```

Or via appsettings (the lib resolves the type via `Type.GetType(string)` and replaces the default factory):

```jsonc
"Caching": {
  "Connections": {
    "Redis": {
      "ConnectionMultiplexerFactoryType":
        "MyApp.Telemetry.OpenTelemetryConnectionMultiplexerFactory, MyApp.Telemetry"
    }
  }
}
```

## Notes

The factory is called once per multiplexer the lib creates. Most apps have one connection, but if the lib reconnects after a connection failure (planned maintenance, transient failures), the factory is called again with new `ConfigurationOptions` — your `AddConnection` call fires for each new multiplexer, keeping OTel instrumentation current.

`StackExchangeRedisInstrumentation` is resolved via `IServiceProvider.GetService<>(...)` so the factory works even if OTel hasn't been registered yet (the `?.AddConnection(...)` short-circuits). In practice OTel is always registered before the caching builder runs, but the null-safe pattern guards against startup-order bugs.

The custom factory delegates to `redisOptions.Value.ConnectionFactory` if set (rare; typically null), then falls back to `ConnectionMultiplexer.Connect(configuration)`. This means you can layer additional connection customization via the `ConnectionFactory` hook without giving up OTel instrumentation.

The `ConnectionMultiplexerFactoryType` appsettings string is parsed via `Type.GetType(string)`, so it must be the **assembly-qualified name** of your factory class — e.g. `MyApp.Telemetry.OpenTelemetryConnectionMultiplexerFactory, MyApp.Telemetry`. Plain `MyApp.Telemetry.X` without the assembly name won't resolve.

## When not to use

- You've chosen AppInsights — use `.AddTelemetry()` instead. The two paths are mutually exclusive for Redis instrumentation (StackExchange.Redis allows only one profiler callback per multiplexer).
- You're not using OTel at all — the factory is dead code without an `IInstrumentation` to add the connection to.

## See also

- [how-to/telemetry-and-strategies.md](../how-to/telemetry-and-strategies.md)
- [recipes/custom-telemetry-provider.md](custom-telemetry-provider.md)
