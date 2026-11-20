namespace UiPath.Caching.Redis;

internal sealed class SendTrace
{
    private int _sent;
    private int _answered;
    private int _committed;

    public void MarkSent() => Interlocked.Increment(ref _sent);

    public void MarkAnswered(bool result)
    {
        Interlocked.Increment(ref _answered);
        if (result)
        {
            Interlocked.Increment(ref _committed);
        }
    }

    public async ValueTask<bool> AcknowledgeAsync(Task<bool> pending)
    {
        var result = await pending.ConfigureAwait(false);
        MarkAnswered(result);
        return result;
    }

    /// <summary>Committed when any attempt was answered true, otherwise unknown while any send is unanswered, otherwise failed.</summary>
    public StoreOutcome Outcome()
    {
        var committed = Volatile.Read(ref _committed);
        var answered = Volatile.Read(ref _answered);
        var sent = Volatile.Read(ref _sent);
        if (committed > 0)
        {
            return StoreOutcome.Committed;
        }

        if (sent == 0)
        {
            return StoreOutcome.NotSent;
        }

        return answered < sent ? StoreOutcome.Unknown : StoreOutcome.Failed;
    }
}
