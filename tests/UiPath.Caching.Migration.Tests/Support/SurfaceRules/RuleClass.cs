namespace UiPath.Caching.Migration;

internal enum RuleClass
{
    /// <summary>Answered by the router itself.</summary>
    Local,

    /// <summary>Routed with the migration rules, the local tier, the pending window and the events.</summary>
    Routed,

    /// <summary>Sent to the active backend: the library when it serves the group.</summary>
    Backend,
}
