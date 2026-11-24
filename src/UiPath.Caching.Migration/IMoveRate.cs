namespace UiPath.Caching.Migration;

/// <summary>How fast a scan unit moves keys. The first mover runs at a fixed safe rate; the health and memory rules that adapt it are a later change, which supplies another implementation.</summary>
internal interface IMoveRate
{
    /// <summary>Waits as long as the unit must before it moves another <paramref name="keys"/> keys, counted from the batch that has just been moved.</summary>
    ValueTask WaitAsync(int keys, CancellationToken cancellationToken);
}
