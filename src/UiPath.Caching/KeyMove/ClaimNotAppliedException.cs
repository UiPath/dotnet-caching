namespace UiPath.Caching.KeyMove;

/// <summary>A bound write that the server discarded before running it (EXECABORT for a redirect or an unknown queue-time error); the attempt starts over.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3871:Exception types should be \"public\"", Justification = "Internal retry signal caught inside the library by the claim store and the stage driver loop; it never reaches the public API.")]
internal sealed class ClaimNotAppliedException(string message, Exception inner) : Exception(message, inner);
