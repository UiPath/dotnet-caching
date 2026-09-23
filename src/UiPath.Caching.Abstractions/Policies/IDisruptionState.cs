namespace UiPath.Caching.Policies;

/// <summary>Whether the tier behind a pipeline is disrupted.</summary>
public interface IDisruptionState
{
    bool InProgress { get; }

    /// <summary>What the tier relaxes its timeouts to while disrupted, if anything.</summary>
    TimeSpan? SuggestedTimeout { get; }
}
