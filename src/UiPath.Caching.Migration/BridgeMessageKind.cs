namespace UiPath.Caching.Migration;

public enum BridgeMessageKind
{
    /// <summary>One key of one cache changed or was removed: needs <see cref="BridgeMessage.CacheName"/> and <see cref="BridgeMessage.Key"/>.</summary>
    Key,

    /// <summary>A tag was invalidated: needs <see cref="BridgeMessage.Tag"/>. The router keeps no tags, so it drops every cache's local copies.</summary>
    Tag,

    /// <summary>One cache was cleared: needs <see cref="BridgeMessage.CacheName"/>.</summary>
    Clear,
}
