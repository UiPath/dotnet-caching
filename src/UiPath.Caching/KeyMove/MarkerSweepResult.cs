namespace UiPath.Caching.KeyMove;

/// <summary>What one pass of the marker sweeper did: markers seen, obsolete ones removed, superseded copies deleted, and live markers whose writer was gone removed.</summary>
internal readonly record struct MarkerSweepResult(int Seen, int Obsolete, int Helped, int Orphans)
{
    public bool DidWork => Obsolete + Helped + Orphans > 0;
}
