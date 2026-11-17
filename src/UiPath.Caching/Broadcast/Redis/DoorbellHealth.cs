namespace UiPath.Caching.Broadcast.Redis;

/// <summary>What a read told the waiter about the doorbell.</summary>
internal enum DoorbellHealth
{
    Unchanged,

    /// <summary>Entries of other nodes keep arriving unannounced: the waits are the poll interval from now on.</summary>
    Missed,

    /// <summary>Doorbells deliver again: the waits are back to the notified interval.</summary>
    Recovered,
}
