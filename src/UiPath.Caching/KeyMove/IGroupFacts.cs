namespace UiPath.Caching.KeyMove;

/// <summary>What the router registration and the app configuration say about a group, for the gates and the step logic.</summary>
internal interface IGroupFacts
{
    /// <summary>Configuration attests the release floor in place of start consent.</summary>
    bool ReleaseFloorAttested { get; }

    /// <summary>The group has a cache without an origin, which gets the S2 barrier.</summary>
    bool HasNoOrigin(string group, IReadOnlyList<string> cacheIds);

    bool TakesClaim(string group);

    string ClaimDescriptor(string group);

    /// <summary>The longest <c>MaxSourceTtl</c> of the group's caches, which caps the DRAINED soak; null when unknown.</summary>
    TimeSpan? LongestSourceTtl(string group);

    /// <summary>Every cache of the group registered <c>AcceptRefill</c>, which X11 needs.</summary>
    bool AllAcceptRefill(string group, IReadOnlyList<string> cacheIds);

    /// <summary>A key move must wait for an active format change to settle.</summary>
    bool FormatSettled(string group);

    /// <summary>A settled subscriber count is insufficient when a pattern or shard subscription can hide nodes.</summary>
    bool NodeListComplete(string group);

    /// <summary>The estimate includes native keys, fences, and control records.</summary>
    bool MemoryFits(string group, long estimatedBytes, double headroom);
}
