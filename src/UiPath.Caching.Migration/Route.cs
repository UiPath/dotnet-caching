namespace UiPath.Caching.Migration;

internal readonly record struct Route(RoutePath Path, string Reason)
{
    public const string Flag = "flag";
    public const string PredicateError = "predicate_error";
    public const string OtherClientRead = "other_client_read";
    public const string OtherClientUnreadable = "other_client_unreadable";

    public RoutePath Other => Path == RoutePath.Library ? RoutePath.Source : RoutePath.Library;
}
