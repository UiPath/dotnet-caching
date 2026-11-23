namespace UiPath.Caching.KeyMove;

internal enum StateAnswer
{
    /// <summary>No definitive answer yet: the read failed, timed out or has not run.</summary>
    Unknown,

    Absent,

    Present,
}
