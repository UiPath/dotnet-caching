using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Tests.Fakes;

/// <summary>A logger that keeps what it was told, at every level.</summary>
internal sealed class RecordedLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _records = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _records.Add((logLevel, formatter(state, exception)));
        }
    }
}
