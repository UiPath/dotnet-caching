namespace UiPath.Caching.Migration;

/// <summary>The one place that says which setting decides a call's path; <see cref="Order"/> is what the startup banner and the documentation list.</summary>
internal static class RoutePrecedence
{
    public static IReadOnlyList<string> Order { get; } = ["pin (UsePinWhen, or a switched group)", "bypass (flag, explicit request, library not started)", "group predicate"];

    /// <summary>No read of the switch state is recent enough to tell whether the group is switched: the call is not answered by either client.</summary>
    public static Route Unknown { get; } = new(RoutePath.Library, Route.PinStateUnknown, Tier: false, NoFallback: true, Hold: true);

    /// <summary>The library, whatever the bypass and the group predicate say, and never the source.</summary>
    public static Route Pinned(bool tier) => new(RoutePath.Library, Route.Pinned, Tier: tier, NoFallback: true);

    public static Route Resolve(bool inBypass, bool libraryDown, FlagState flag, bool tierOpen, Func<string, bool> predicate, string group)
    {
        // Bypass wins over every other setting: the call is on the source, with no shared tier and no library.
        if (inBypass || flag != FlagState.Off)
        {
            var reason = Route.Bypassed;
            if (libraryDown)
            {
                reason = Route.LibraryUnavailable;
            }
            else if (flag == FlagState.Failed)
            {
                reason = Route.BypassFlagError;
            }

            return new Route(RoutePath.Source, reason, Bypass: true, Tier: false);
        }

        try
        {
            return new Route(predicate(group) ? RoutePath.Library : RoutePath.Source, Route.Flag, Tier: tierOpen);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Route(RoutePath.Source, Route.PredicateError, Tier: tierOpen);
        }
    }
}
