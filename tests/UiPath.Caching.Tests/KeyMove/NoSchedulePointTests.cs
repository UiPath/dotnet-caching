using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class NoSchedulePointTests
{
    [Fact]
    public async Task The_production_schedule_point_completes_at_once_and_allocates_nothing()
    {
#if DEBUG
        Assert.Skip("allocation counts are only stable in Release");
#endif
        ISchedulePoint point = NoSchedulePoint.Instance;
        var steps = Enum.GetValues<SchedulePointStep>();
        await Pass(point, steps);

        var fewest = long.MaxValue;
        for (var round = 0; round < 10 && fewest > 0; round++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            await Pass(point, steps);
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        fewest.Should().Be(0);
    }

    [Fact]
    public async Task Every_step_completes_synchronously()
    {
        foreach (var step in Enum.GetValues<SchedulePointStep>())
        {
            var pending = NoSchedulePoint.Instance.AtAsync(step);
            var completedSynchronously = pending.IsCompletedSuccessfully;
            await pending;
            completedSynchronously.Should().BeTrue();
        }
    }

    private static async Task Pass(ISchedulePoint point, SchedulePointStep[] steps)
    {
        for (var i = 0; i < 1_000; i++)
        {
            foreach (var step in steps)
            {
                await point.AtAsync(step);
            }
        }
    }
}
