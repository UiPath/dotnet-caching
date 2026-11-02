namespace UiPath.Caching.Broadcast;

/// <summary>
/// An optional capability of an <see cref="IEventFormatterProxy{T}"/>: telling a payload's source without decoding it,
/// so a transport drops a node's own messages unread.
/// </summary>
public interface IEventSourceMatcherProvider
{
    /// <summary>A matcher for payloads from <paramref name="source"/>, or null when this formatter cannot tell without a decode.</summary>
    IEventSourceMatcher? CreateSourceMatcher(Uri source);
}
