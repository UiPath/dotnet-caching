namespace UiPath.Caching.Migration;

/// <param name="Change">What the swap reported.</param>
/// <param name="Write">Committed, NotSent, or Unknown when it may have written, which the router settles.</param>
internal readonly record struct SwapResult<T>(ChangeResult<T> Change, WriteResult Write);
