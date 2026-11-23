namespace UiPath.Caching.KeyMove;

internal sealed class NoSchedulePoint : ISchedulePoint
{
    public static readonly NoSchedulePoint Instance = new();

    private NoSchedulePoint()
    {
    }

    public ValueTask AtAsync(SchedulePointStep step) => default;
}
