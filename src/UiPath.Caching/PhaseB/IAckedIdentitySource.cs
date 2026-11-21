namespace UiPath.Caching.PhaseB;

/// <summary>What the node advertiser reads: the identity this node acknowledged for a group.</summary>
internal interface IAckedIdentitySource
{
    /// <returns>The acknowledged identity, or null for none, which the advertiser publishes as <c>&lt;g&gt;@0</c>.</returns>
    AckedIdentity? AckedFor(string group);
}
