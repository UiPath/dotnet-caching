namespace UiPath.Caching.Broadcast;

public static class EventExtensions
{
    public static bool SameSource(this IEvent ev, Uri currentSource) =>
        Uri.Compare(currentSource, ev.Source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.InvariantCultureIgnoreCase) == 0;
}

