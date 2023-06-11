namespace UiPath.Platform.Caching.Broadcast.Redis;

public static class EventExtensions
{
    public static bool IsValid(this IEvent ev, Uri machineUri) =>
        ev.IsValid() && Uri.Compare(machineUri, ev.Source, UriComponents.AbsoluteUri, UriFormat.SafeUnescaped, StringComparison.InvariantCultureIgnoreCase) != 0;
}

