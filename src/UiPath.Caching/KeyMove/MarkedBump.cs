namespace UiPath.Caching.KeyMove;

/// <summary>The reply of BUMP setm: the code, and for a committed one the record's field count and the exact marker value that was written.</summary>
internal readonly record struct MarkedBump(long Code, long Length = 0, string? Marker = null)
{
    public bool Committed => Code >= 0;
}
