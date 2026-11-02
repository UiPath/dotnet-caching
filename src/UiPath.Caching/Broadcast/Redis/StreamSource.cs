namespace UiPath.Caching.Broadcast.Redis;

/// <summary>The form of an event's source a stream entry carries beside the payload.</summary>
internal static class StreamSource
{
    /// <summary>
    /// The text <see cref="EventExtensions.SameSource"/> compares: the absolute URI, safe-unescaped; a relative one as
    /// written. Two sources equal here are the same to it, so a reader that finds its own may drop the entry unread.
    /// </summary>
    public static string Of(Uri source) => source.IsAbsoluteUri ? source.GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped) : source.OriginalString;
}
