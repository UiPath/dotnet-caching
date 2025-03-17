# Telemetry Instrumentation
This page is to demonstrate the telemetry instrumentation in this library.

Currently this library is supporting either AppInsights or OpenTelemetry instrumentation (sorry no dual write due to the technical limit from StackExchangeRedis library) and Appinsights instrumentation is the default


## Application Insights
### Usage
#### Program.cs 
```csharp
builder.Host.ConfigureCaching(
    cachingBuilder =>
    {
        cachingBuilder
        .AddRedisConnection()
        .AddBroadcast()
        .AddRedis()
        .AddInMemoryRedis()
        .AddMemory()
        .AddResilienceStrategies()
        .AddCloudEvents();
    }
);

builder.Services
    .AddAspNetCoreTelemetry(builder.Configuration)
    .AddHttpDynamicFilter()
    .AddRequestDynamicFilter()
    .AddTraceDynamicFilter()
    .AddRedisDynamicFilter() // <-- this is the extension method to add the RedisDynamicFilterProcessor
    .WithAdaptiveSampling()
    .WithBuiltinProcessors()
    .WithDefaultLogMinLevelAdjustment();

app.UseHttpsRedirection()
    .UseAuthorization()
    .UseTelemetry()
    .UseMiddleware<TelemetryContextMiddleware>()
    .UseMiddleware<RedisProfilerMiddleware>();
```
#### appSettings.json
```json
{
    "Caching": {
        "AppShortName": "app",
        "ConnectionMonitorEnabled": true,
        "Connections": {
            "Redis": {
                "ConnectionString": "localhost:6379,abortConnect=false",
                "ConnectionStringExtraParams": "connectRetry=2,keepAlive=30,syncTimeout=2500,connectTimeout=2500",
                "ProfilerEnabled": true,
                "MaintainerEnabled": true,
                "MaintainerCheckInterval": "0:01:00",
                "MaintainerTrimInterval": "0:05:00",
                "MaintainerQuarantineInterval": "0:02:00"
            }
        }
    },
}
```


## OpenTelemetry
The Redis telemetry is auto collected by OpenTelemetry SDK but there is still change needed in the caching library.

> You may still keep your existing Application Insights library registration, but the Redis instrumentation is mutually exclusie

### Usage
#### Program.cs
```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(builder =>
    {
        builder.AddAspNetCoreInstrumentation()
          .AddHttpClientInstrumentation()
          .AddRedisInstrumentation(inst =>
          {
              inst.SetVerboseDatabaseStatements = true;
          })
          .ConfigureRedisInstrumentation(instrumentation => { })
          .AddConsoleExporter();
    });

builder.Host.ConfigureCaching(
        cachingBuilder =>
        {
            cachingBuilder.Services.AddTransient<IConnectionMultiplexerFactory, OpenTelemetryConnectionMultiplexerFactory>(); // The service is responsible for implementing the OpenTelemetryConnectionMultiplexerFactory but it does not neccesarry to register the class here. More details in below

            cachingBuilder
            .AddRedisConnection()
            .AddBroadcast()
            .AddRedis()
            .AddInMemoryRedis()
            .AddMemory()
            .AddResilienceStrategies()
            .AddCloudEvents()
            .AddTelemetry(); // You may still keep this but it won't be used anywhere
        }
    );

// You may still keep the existing AppInsights registration
builder.Services
    .AddAspNetCoreTelemetry(builder.Configuration)
    .AddHttpDynamicFilter()
    .AddRequestDynamicFilter()
    .AddTraceDynamicFilter()
    .AddRedisDynamicFilter() // <-- this is the extension method to add the RedisDynamicFilterProcessor
    .WithAdaptiveSampling()
    .WithBuiltinProcessors()
    .WithDefaultLogMinLevelAdjustment();

app.UseHttpsRedirection()
    .UseAuthorization()
    .UseTelemetry()
    .UseMiddleware<TelemetryContextMiddleware>();
```

#### Implementation of OpenTelemetryConnectionMultiplexerFactory
```csharp
public class OpenTelemetryConnectionMultiplexerFactory(IOptions<RedisConnectionOptions> redisOptions, IServiceProvider serviceProvider) : IConnectionMultiplexerFactory
{
    public IConnectionMultiplexer Create(ConfigurationOptions configuration)
    {
        var cnn = redisOptions.Value.ConnectionFactory?.Invoke(configuration) ?? ConnectionMultiplexer.Connect(configuration);
        var instrumentation = serviceProvider.GetService<StackExchangeRedisInstrumentation>();
        instrumentation?.AddConnection(cnn);
        return cnn;
    }
}
```

#### appSettings.json
```json
"Caching": {
  "AppShortName": "app",
  "ConnectionMonitorEnabled": true,
  "Connections": {
    "Redis": {
      "ConnectionString": "localhost:6379,abortConnect=false",
      "ConnectionStringExtraParams": "connectRetry=2,keepAlive=30,syncTimeout=2500,connectTimeout=2500",
      "ProfilerEnabled": false,
      "MaintainerEnabled": true,
      "MaintainerCheckInterval": "0:01:00",
      "MaintainerTrimInterval": "0:05:00",
      "MaintainerQuarantineInterval": "0:02:00",
      "ConnectionMultiplexerFactoryType": "UiPath.Platform.Sample.AspNetCore.OpenTelemetryConnectionMultiplexerFactory, UiPath.Platform.Sample.AspNetCore, Culture=neutral, PublicKeyToken=null" // By providing the full name of the OpenTelemetryConnectionMultiplexerFactory. The registration will be automatically finished
    }
  }
},
```

