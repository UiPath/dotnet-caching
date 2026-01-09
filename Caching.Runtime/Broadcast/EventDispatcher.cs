using System.Reactive.Subjects;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using UiPath.Platform.Caching.Telemetry;

namespace UiPath.Platform.Caching.Broadcast;

internal sealed partial class EventDispatcher<T> : IDisposable
    where T : IEvent
{
    private readonly TopicKey _topicKey;
    private readonly ChannelReader<T> _reader;
    private readonly ISubject<T> _subject;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly CancellationToken _cancellationToken;
    private bool _disposed;

    public EventDispatcher(TopicKey topicKey,
        ChannelReader<T> reader,
        ISubject<T> subject,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        _topicKey = topicKey;
        _reader = reader;
        _subject = subject;
        _logger = logger;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationToken = _stopTokenSource.Token;
        ConsumeTask = Task.Run(Consume, _cancellationToken);
    }

    internal Task ConsumeTask { get; }

    private async Task Consume()
    {
        while (await _reader.WaitToReadAsync(_cancellationToken).ConfigureAwait(false))
        {
            while (_reader.TryRead(out var item))
            {
                _subject.OnNext(item);
            }
        }
        LogStoppedConsuming(_topicKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stopTokenSource?.Cancel();
        _stopTokenSource?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopped consuming from topic {TopicKey}")]
    private partial void LogStoppedConsuming(TopicKey topicKey);
}
