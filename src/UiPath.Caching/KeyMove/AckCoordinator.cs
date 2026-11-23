namespace UiPath.Caching.KeyMove;

/// <summary>Moves the gate to the identity the snapshot shows durable, and acknowledges it once every unit of an older identity completed or was re-admitted.</summary>
internal sealed class AckCoordinator(GroupStateMonitor monitor, EpochGate gate, StageDriverOptions options, TimeProvider time)
{
    /// <returns>True when the node now acknowledges the identity it acts under.</returns>
    public bool Tick(string group)
    {
        if (monitor.RulesFor(group).Acting is not { } acting)
        {
            return false;
        }

        gate.Switch(group, acting.Ep, acting.Id);
        return gate.OlderUnits(group) == 0 && monitor.Acknowledge(group, acting.Ep, acting.Id);
    }

    public async Task RunAsync(IReadOnlyList<string> groups, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                foreach (var group in groups)
                {
                    Tick(group);
                }

                await Task.Delay(options.AckInterval, time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
