namespace UiPath.Caching.Migration;

internal readonly record struct Route(RoutePath Path, string Reason, bool Bypass = false, bool Tier = true)
{
    public const string Flag = "flag";
    public const string PredicateError = "predicate_error";
    public const string Bypassed = "bypass";
    public const string BypassFlagError = "bypass_flag_error";
    public const string LibraryUnavailable = "library_unavailable";
    public const string OtherClientRead = "other_client_read";
    public const string OtherClientUnreadable = "other_client_unreadable";

    public RoutePath Other => Path == RoutePath.Library ? RoutePath.Source : RoutePath.Library;
}
