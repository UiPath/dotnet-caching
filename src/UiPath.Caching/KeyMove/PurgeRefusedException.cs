namespace UiPath.Caching.KeyMove;

/// <summary>A destructive data operation that stopped because its identity, lease or evidence is gone.</summary>
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
