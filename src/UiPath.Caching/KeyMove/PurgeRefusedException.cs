namespace UiPath.Caching.KeyMove;

/// <summary>A destructive data operation that stopped because its identity, lease or evidence is gone.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3871:Exception types should be \"public\"", Justification = "Internal signal caught inside the library (purge outcome and stage driver loop); it never reaches the public API.")]
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
