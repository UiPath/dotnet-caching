namespace UiPath.Caching.Migration;

/// <summary>The one place that says which setting decides a call's path; <see cref="Order"/> is what the startup banner and the documentation list.</summary>
internal static class RoutePrecedence
{
    public static IReadOnlyList<string> Order { get; } = ["bypass (flag, explicit request, library not started)", "group predicate", "pin (not available in this version)"];

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
