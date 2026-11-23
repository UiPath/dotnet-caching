namespace UiPath.Caching.KeyMove;

internal interface IStateReader
{
    /// <returns>The state, or null when the hash is definitively absent on the master. A failed or timed-out read throws; it is never an answer.</returns>
    Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken);

    /// <returns>The control record at the probe slot, or null when there is none.</returns>
    Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken);

    /// <returns>The record with the highest <c>ep</c> among a sample of every master's slots, or null when none holds one.</returns>
    Task<ControlSample?> SampleAsync(string group, CancellationToken cancellationToken);
}
