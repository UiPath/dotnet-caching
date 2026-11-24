namespace UiPath.Caching.KeyMove;

/// <summary>Tells the key-move mover how hard it may push Redis: it raises its rate on green, halves it on amber and pauses on red.</summary>
/// <remarks>A call that throws, times out, returns null or returns an undefined state is read as red.</remarks>
internal interface IKeyMoveHealthSource
{
    /// <summary>Reads the current health.</summary>
    ValueTask<KeyMoveHealth> GetHealthAsync(CancellationToken cancellationToken);
}
