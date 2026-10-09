namespace UiPath.Caching.Broadcast;

/// <summary>Turns events dropped at a full dispatcher channel into one expiry at a time, off the thread that dropped them.</summary>
internal sealed partial class DroppedEvents(TopicKey topicKey, Action expire, ILogger logger)
{
    private const int Idle = 0;
    private const int Running = 1;
    private const int Rerun = 2;

    private readonly ILogger _logger = logger;
    private int _state;

    public void Dropped()
    {
        while (true)
        {
            switch (Volatile.Read(ref _state))
            {
                case Idle when Interlocked.CompareExchange(ref _state, Running, Idle) == Idle:
                    ThreadPool.UnsafeQueueUserWorkItem(static self => self.Run(), this, preferLocal: false);
                    return;
                case Running when Interlocked.CompareExchange(ref _state, Rerun, Running) == Running:
                case Rerun:
                    return;
            }
        }
    }

    private void Run()
    {
        while (true)
        {
            LogEventsDropped(topicKey);
            try
            {
                expire();
            }
            catch (Exception ex)
            {
                LogExpireFailed(ex, topicKey);
            }

            if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running)
            {
                return;
            }

            // A drop came in during that expiry: run once more, on this worker.
            Volatile.Write(ref _state, Running);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Topic {TopicKey} dropped events at its full dispatcher channel; expiring what it keeps")]
    private partial void LogEventsDropped(TopicKey topicKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Expiring topic {TopicKey} after dropped events failed")]
    private partial void LogExpireFailed(Exception ex, TopicKey topicKey);
}
