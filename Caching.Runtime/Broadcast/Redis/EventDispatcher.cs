using System.Reactive.Subjects;
using System.Threading.Channels;

namespace UiPath.Platform.Caching.Broadcast.Redis;

internal sealed class EventDispatcher<T> : IDisposable
    where T : IEvent
{
    private readonly TopicKey _topicKey;
    private readonly ChannelReader<T> _reader;
    private readonly ISubject<T> _subject;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopTokenSource;
    private readonly CancellationToken _cancellationToken;
#pragma warning disable IDE0052 // Remove unread private members
    [SuppressMessage("SonarLint.Rule", "S4487:\"Unread\" members should be removed")]
    private readonly Task _consumeTask;
#pragma warning restore IDE0052 // Remove unread private members

    public EventDispatcher(TopicKey topicKey, ChannelReader<T> reader, ISubject<T> subject, ILogger logger, CancellationToken cancellationToken)
    {
        _topicKey = topicKey;
        _reader = reader;
        _subject = subject;
        _logger = logger;
        _stopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellationToken = _stopTokenSource.Token;
        _consumeTask = Task.Run(Consume, _cancellationToken);
    }

    private async Task Consume()
    {
        while(await _reader.WaitToReadAsync(_cancellationToken).ConfigureAwait(false))
        {
            while (_reader.TryRead(out var item))
            {
                _subject.OnNext(item);
            }
        }
        _logger.LogDebug("Stopped consuming from topic {}", _topicKey);
    }

    public void Dispose()
    {
        _stopTokenSource.Cancel();
    }
}
