using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using UiPath.Caching.CloudEvents;
using UiPath.Caching.Config;
using UiPath.Caching.OpenTelemetry;
using UiPath.Caching.Polly;
using UiPath.Caching.Redis;
using UiPath.Caching.Sample;

//Change in appsettings.json /Caching/DefaultCache with values from UiPath.Caching.KnownCacheNames in order to test implementations via controllers
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Logging.AddConsole();

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(CachingTelemetryProvider.ActivitySourceName)
        .AddRedisInstrumentation(ConfigureRedisInstrumentation))
    .WithMetrics(metrics => metrics
        .AddMeter(CachingTelemetryProvider.MeterName));

builder.Host
    .ConfigureCaching(cachingBuilder =>
    {
        cachingBuilder.Services.AddTransient<IConnectionMultiplexerFactory, OpenTelemetryConnectionMultiplexerFactory>();

        cachingBuilder
            .AddRedisConnection()
            .AddBroadcast()
            .AddRedis()
            .AddInMemoryRedis()
            .AddMemory()
            .AddResilienceStrategies()
            .AddCloudEvents()
            .AddOpenTelemetry();
    });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddRequestTimeouts(opt => opt.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromMilliseconds(100),
    TimeoutStatusCode = 503
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/healthz");
app.MapDefaultEndpoints();

app.MapControllers();

app.Run();

static void ConfigureRedisInstrumentation(StackExchangeRedisInstrumentationOptions options)
{
    options.SetVerboseDatabaseStatements = true;
    options.Enrich = (activity, context) =>
    {
        var command = context.ProfiledCommand.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        activity.SetTag("redis.command", command);
        activity.DisplayName = $"Redis {GetRedisCommandName(command)}";
    };
}

static string GetRedisCommandName(string command)
{
    var separatorIndex = command.IndexOf(' ', StringComparison.Ordinal);
    return separatorIndex > 0
        ? command[..separatorIndex]
        : command;
}
