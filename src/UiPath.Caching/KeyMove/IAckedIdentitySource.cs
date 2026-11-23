namespace UiPath.Caching.KeyMove;

internal interface IAckedIdentitySource
{
    /// <returns>The acknowledged identity, or null for none, which the advertiser publishes as <c>&lt;g&gt;@0</c>.</returns>
    AckedIdentity? AckedFor(string group);
}
