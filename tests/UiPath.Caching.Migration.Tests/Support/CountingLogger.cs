using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration.Tests.Support;

/// <summary>Counts the warnings logged to it.</summary>
internal sealed class CountingLogger : ILogger
{
    private int _warnings;

    public int Warnings => Volatile.Read(ref _warnings);

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            Interlocked.Increment(ref _warnings);
        }
    }
}
