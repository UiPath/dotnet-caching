using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;
using UiPath.Caching.Logging;
using UiPath.Caching.Telemetry;

namespace UiPath.Caching.Tests.Logging;

/// <summary>The components that log a key from outside the cache classes: the multilayer tier and its broadcast change token.</summary>
public class MaskedLogSiteTests
{
    private const string SecretKey = "session:cosmin";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cache_miss_line_follows_the_policy(bool masked)
    {
        var logs = new CapturingLoggerProvider();
        using var provider = BuildContainer(masked, logs);
        var cache = provider.GetRequiredService<ICache<string>>();

        await cache.GetOrAddAsync(SecretKey, _ => Task.FromResult<string?>("v"), TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var line = logs.Lines.Should().ContainSingle(l => l.Contains("Cache missed")).Subject;
        if (masked)
        {
            line.Should().Contain("ses****").And.NotContain(SecretKey);
        }
        else
        {
            line.Should().Contain(SecretKey);
        }
    }

    [Fact]
    public void The_change_token_masks_the_key_it_waits_on()
    {
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var topic = Substitute.For<ITopic<ICacheEvent>>();
        topic.TopicKey.Returns(new TopicKey("topic"));

        _ = new ChangeToken<byte[]>(
            SecretKey,
            topic,
            source: null,
            new SystemJsonByteSerializerProxy(null),
            loggerFactory.CreateLogger<ChangeToken<byte[]>>(),
            NullTelemetryProvider.Instance,
            acceptedEvents: null,
            new KeyMasker(new PrefixKeyMaskingPolicy(), KnownCacheProviderNames.InMemoryRedis),
            entryType: typeof(byte[]),
            callerKey: SecretKey);

        logs.Lines.Should().Contain(l => l.Contains("ses****")).And.NotContain(l => l.Contains(SecretKey));
    }

    private static ServiceProvider BuildContainer(bool masked, CapturingLoggerProvider logs) =>
        new ServiceCollection()
            .AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace))
            .AddCaching(
                b =>
                {
                    b.AddMemory(_ => { });
                    if (masked)
                    {
                        b.AddKeyMasking();
                    }
                },
                o =>
                {
                    o.AppShortName = "app";
                    o.DefaultCache = KnownCacheProviderNames.InMemory;
                })
            .BuildServiceProvider();

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLoggerInstance(_lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLoggerInstance(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
