namespace UiPath.Caching.KeyMove;

/// <summary>A key that the sweep of one cache found owned by it and by another cache, so whose data it is cannot be told.</summary>
internal sealed class AmbiguousKeyException(string message) : InvalidOperationException(message);
