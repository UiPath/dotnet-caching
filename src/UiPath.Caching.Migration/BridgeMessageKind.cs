namespace UiPath.Caching.Migration;

public enum BridgeMessageKind
{
    Key,

    /// <summary>The router keeps no tags, so a received tag clears every cache's local copies.</summary>
    Tag,

    Clear,
}
