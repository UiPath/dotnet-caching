using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Trace;
using UiPath.Platform.Caching.CloudEvents;
using UiPath.Platform.Caching.Config;
using UiPath.Platform.Caching.Polly;
using UiPath.Platform.Caching.Redis;
using UiPath.Platform.Sample.AspNetCore;

//Change in appsettings.json /Caching/DefaultCache with values from UiPath.Platform.Caching.KnownCacheNames in order to test implementations via controllers
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Logging.AddConsole();

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddRedisInstrumentation(ConfigureRedisInstrumentation));

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
            .AddCloudEvents();
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
