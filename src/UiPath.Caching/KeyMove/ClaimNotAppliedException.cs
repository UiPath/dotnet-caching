namespace UiPath.Caching.KeyMove;

/// <summary>A bound write that the server discarded before running it (EXECABORT for a redirect or an unknown queue-time error); the attempt starts over.</summary>
internal sealed class ClaimNotAppliedException(string message, Exception inner) : Exception(message, inner);
