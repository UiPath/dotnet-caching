using StackExchange.Redis;

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
        bool result;
        try
        {
            result = await pending.ConfigureAwait(false);
        }
        catch (RedisConnectionException ex) when (NeverWritten(ex))
        {
            // The client never wrote the command: this attempt was not sent after all, so it cannot leave the outcome unknown.
            Interlocked.Decrement(ref _sent);
            throw;
        }

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

    /// <summary>Only what proves the command never reached a socket; a status of unknown is ambiguous and stays a send.</summary>
    private static bool NeverWritten(RedisConnectionException ex) =>
        ex.CommandStatus == CommandStatus.WaitingToBeSent
        || ex.Message.StartsWith("No connection is active/available", StringComparison.Ordinal);
}
