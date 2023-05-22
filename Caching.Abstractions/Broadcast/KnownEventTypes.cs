namespace UiPath.Platform.Caching.Broadcast;

public static class KnownEventTypes
{
    public const string ClearCache = "ClearCache";

    //add you custom event types here
    private static readonly ISet<string> All = new HashSet<string>(new[] { ClearCache }, StringComparer.OrdinalIgnoreCase);

    public static void Add(string eventType) => All.Add(eventType);

    public static bool IsKnown(string? eventType) => !string.IsNullOrEmpty(eventType) && All.Contains(eventType);
}
