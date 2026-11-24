namespace UiPath.Caching.KeyMove;

/// <summary>What the retire gate saw: no marker of the retiring generation is left, or how many the last complete pass met.</summary>
/// <param name="Complete">The pass that gave this verdict covered every slot; an unfinished one says nothing about the markers.</param>
/// <param name="BlockedFor">How long complete passes have been finding markers.</param>
internal readonly record struct RetireVerdict(bool Clear, int Remaining, bool Complete = true, TimeSpan BlockedFor = default)
{
    public static RetireVerdict Done { get; } = new(true, 0);
}
