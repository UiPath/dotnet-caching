namespace UiPath.Caching.Migration;

/// <summary>A window of the day, in UTC, in which the mover may run; an end before the start wraps past midnight, and equal times mean the whole day.</summary>
internal sealed class MoveWindow
{
    /// <summary>The time of day the window opens.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>The time of day the window closes.</summary>
    public TimeSpan End { get; set; }

    /// <summary>The days the window opens on, by the day its start falls on; null or empty means every day.</summary>
    public DayOfWeek[]? Days { get; set; }

    /// <summary>Whether <paramref name="now"/> falls inside the window.</summary>
    public bool Contains(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        var at = utc.TimeOfDay;
        if (Start == End)
        {
            return DayAllowed(utc.DayOfWeek);
        }

        if (Start < End)
        {
            return at >= Start && at < End && DayAllowed(utc.DayOfWeek);
        }

        // Past midnight: the late part belongs to the day the window opened on, the early part to the day before.
        return at >= Start ? DayAllowed(utc.DayOfWeek) : at < End && DayAllowed(utc.AddDays(-1).DayOfWeek);
    }

    private bool DayAllowed(DayOfWeek day) => Days is not { Length: > 0 } || Array.IndexOf(Days, day) >= 0;
}
