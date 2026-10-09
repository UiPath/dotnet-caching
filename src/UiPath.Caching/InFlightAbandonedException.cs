namespace UiPath.Caching;

/// <summary>The caller running a reservation was cancelled, so its joiners take the key over.</summary>
internal sealed class InFlightAbandonedException : Exception;
