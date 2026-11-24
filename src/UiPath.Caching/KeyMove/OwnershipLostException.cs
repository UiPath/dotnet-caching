using System.Diagnostics.CodeAnalysis;

namespace UiPath.Caching.KeyMove;

/// <summary>Raised before an attempt of a fenced operation when its caller no longer owns the work, so the attempt is not sent.</summary>
[SuppressMessage("csharpsquid", "S3871", Justification = "The exception is an internal ownership signal handled by the mover. Exposing it would add a public API.")]
internal sealed class OwnershipLostException : Exception
{
    public OwnershipLostException()
        : base("The caller no longer owns the work, so no further attempt is sent.")
    {
    }
}
