namespace UiPath.Caching.PhaseB;

internal enum StateAnswer
{
    /// <summary>No definitive answer yet: the read failed, timed out or has not run.</summary>
    Unknown,

    Absent,

    Present,
}
