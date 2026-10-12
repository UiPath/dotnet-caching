namespace UiPath.Caching;

/// <summary>The caller running a reservation was cancelled, so its joiners take the key over.</summary>
[SuppressMessage("Design", "S3871:Exception types should be \"public\"", Justification = "A control signal between callers of one process: it ends a reservation and is caught where the key is taken again, and never reaches a caller of the library.")]
internal sealed class InFlightAbandonedException : Exception;
