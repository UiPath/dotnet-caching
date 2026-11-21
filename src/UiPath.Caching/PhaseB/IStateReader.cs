namespace UiPath.Caching.PhaseB;

internal interface IStateReader
{
    /// <returns>The state, or null when the hash is definitively absent on the master. A failed or timed-out read throws; it is never an answer.</returns>
    Task<StateRecord?> ReadAsync(string group, CancellationToken cancellationToken);

    /// <returns>The control record at the probe slot, or null when there is none.</returns>
    Task<ControlSample?> ProbeAsync(string group, CancellationToken cancellationToken);
}
