namespace UiPath.Caching.KeyMove;

/// <summary>The reply of HSETC setm: the code, the usual HSETC result of a committed write, and the record's field count and the exact marker value that was written.</summary>
/// <param name="Code">1 when committed; <see cref="MoveScripts.IdentityMismatch"/>, <see cref="MoveScripts.RecordFull"/>, <see cref="MoveScripts.Late"/> or <see cref="MoveScripts.NoBinding"/> change nothing.</param>
/// <param name="Result">The new version when counting (0 when the count could not move), otherwise 1; the refusal code when not committed.</param>
internal readonly record struct MarkedHashWrite(long Code, long Result, long Length = 0, string? Marker = null)
{
    public bool Committed => Code >= 0;
}
