namespace UiPath.Caching.PhaseB;

/// <summary>What the router registration and the app configuration say about a group, for the gates and the step logic.</summary>
internal interface IGroupFacts
{
    /// <summary>G5 when it is also fixed by configuration; the stored start consent attests it otherwise.</summary>
    bool ReleaseFloorAttested { get; }

    /// <summary>The group has a cache without an origin, which gets the S2 barrier.</summary>
    bool HasNoOrigin(string group, IReadOnlyList<string> cacheIds);

    /// <summary>The group takes the move claim at X1 (a router group).</summary>
    bool TakesClaim(string group);

    /// <summary>The kind of claim the group takes, as the <c>mv</c> descriptor.</summary>
    string ClaimDescriptor(string group);

    /// <summary>The longest <c>MaxSourceTtl</c> of the group's caches, which caps the DRAINED soak; null when unknown.</summary>
    TimeSpan? LongestSourceTtl(string group);

    /// <summary>Every cache of the group registered <c>AcceptRefill</c>, which X11 needs.</summary>
    bool AllAcceptRefill(string group, IReadOnlyList<string> cacheIds);

    /// <summary>G10: the format state of the group is settled, so a move never runs under a format flip.</summary>
    bool FormatSettled(string group);

    /// <summary>G3: the node list is complete (the subscriber count is settled and no pattern or sharded subscriber hides nodes).</summary>
    bool NodeListComplete(string group);

    /// <summary>G9: the estimated size of N, plus the fence keys and control records, fits the memory plan with the headroom.</summary>
    bool MemoryFits(string group, long estimatedBytes, double headroom);
}
