namespace UiPath.Caching.KeyMove;

internal enum SweepMode
{
    /// <summary>The forward run: obsolete markers by SMGC, and current ones whose writer is gone by help.</summary>
    Orphans,

    /// <summary>S1(rev): the markers of the retiring generation are helped at once and the others removed by SMGC.</summary>
    Retire,
}
