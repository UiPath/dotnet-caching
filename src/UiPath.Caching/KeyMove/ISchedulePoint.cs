namespace UiPath.Caching.KeyMove;

/// <summary>Called before each Redis command of a promoter, mover, writer or clear; a test harness reorders them, production does nothing.</summary>
internal interface ISchedulePoint
{
    ValueTask AtAsync(SchedulePointStep step);
}
