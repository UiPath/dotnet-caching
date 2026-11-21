namespace UiPath.Caching.Migration;

/// <param name="NoFallback">The call never takes the other client, whatever the first one says.</param>
/// <param name="Hold">The call is answered unavailable without asking either client.</param>
internal readonly record struct Route(RoutePath Path, string Reason, bool Bypass = false, bool Tier = true, bool NoFallback = false, bool Hold = false)
{
    public const string Flag = "flag";
    public const string PredicateError = "predicate_error";
    public const string Bypassed = "bypass";
    public const string BypassFlagError = "bypass_flag_error";
    public const string LibraryUnavailable = "library_unavailable";
    public const string OtherClientRead = "other_client_read";
    public const string OtherClientUnreadable = "other_client_unreadable";
    public const string Pinned = "pin";
    public const string PinStateUnknown = "pin_state_unknown";

    public RoutePath Other => Path == RoutePath.Library ? RoutePath.Source : RoutePath.Library;
}
