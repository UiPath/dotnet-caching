using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// Records every schedule point and lets a test run something at the n-th time a step is reached.
internal sealed class RecordingSchedule : ISchedulePoint
{
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private readonly List<SchedulePointStep> _steps = [];

    public Func<SchedulePointStep, int, Task>? Hook { get; set; }

    public IReadOnlyList<SchedulePointStep> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _steps.Clear();
        }
    }

    public async ValueTask AtAsync(SchedulePointStep step)
    {
        int nth;
        lock (_gate)
        {
            _steps.Add(step);
            nth = _steps.Count(s => s == step);
        }

        if (Hook is { } hook)
        {
            await hook(step, nth);
        }
    }
}
