namespace UiPath.Caching.KeyMove;

/// <summary>The verdict of an <see cref="IKeyMoveHealthSource"/>: a state and the reason for it.</summary>
internal sealed class KeyMoveHealth
{
    /// <summary>Creates a verdict; <paramref name="reason"/> names the signal behind the state, or is empty.</summary>
    public KeyMoveHealth(KeyMoveHealthState state, string reason)
    {
        State = state;
        Reason = reason ?? throw new ArgumentNullException(nameof(reason));
    }

    /// <summary>How much load the mover may put on Redis.</summary>
    public KeyMoveHealthState State { get; }

    /// <summary>A short text for logs and alerts, never for a decision.</summary>
    public string Reason { get; }
}
