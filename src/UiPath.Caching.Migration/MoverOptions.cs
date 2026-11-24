namespace UiPath.Caching.Migration;

/// <summary>The settings of the mover that are not public yet.</summary>
internal sealed record MoverOptions
{
    /// <summary>COUNT of one SCAN call, which is also the batch whose cursor is stored.</summary>
    public int ScanCount { get; init; } = 500;

    /// <summary>How long a unit's lease lives without a renewal.</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The lease is renewed after this many keys, or after a third of its life, whichever comes first.</summary>
    public int RenewEveryKeys { get; init; } = 50;

    /// <summary>How often the host looks at the state and the units, and how long a unit waits for a lease another runner holds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The least time between two SCAN calls of a unit, so a sparse match cannot loop without a pause.</summary>
    public TimeSpan MinScanInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The least time between the starts of two passes of a unit, whichever runner holds it.</summary>
    public TimeSpan PassPause { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A cursor that is not renewed is dropped after this; a finished pass leaves none.</summary>
    public TimeSpan CursorLife { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long the record of a finished pass is kept for the drain check.</summary>
    public TimeSpan PassRecordLife { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The same alert of the mover is raised at most once in this time.</summary>
    public TimeSpan AlertEvery { get; init; } = TimeSpan.FromMinutes(1);
}
