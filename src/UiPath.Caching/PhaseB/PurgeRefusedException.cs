namespace UiPath.Caching.PhaseB;

/// <summary>A destructive data operation stopped because its state identity or lease is gone, or because owned keys kept appearing.</summary>
internal sealed class PurgeRefusedException : Exception
{
    public PurgeRefusedException(string message)
        : base(message)
    {
    }

    public PurgeRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
