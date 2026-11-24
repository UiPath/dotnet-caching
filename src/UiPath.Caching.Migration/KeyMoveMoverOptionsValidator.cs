using Microsoft.Extensions.Options;

namespace UiPath.Caching.Migration;

/// <summary>Checks <see cref="KeyMoveMoverOptions"/> at startup and at each reload.</summary>
internal sealed class KeyMoveMoverOptionsValidator : IValidateOptions<KeyMoveMoverOptions>
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public ValidateOptionsResult Validate(string? name, KeyMoveMoverOptions o)
    {
        var errors = new List<string>();
        Floor(errors, nameof(o.StartKeysPerSecond), o.StartKeysPerSecond, KeyMoveMoverOptions.FloorKeysPerSecond);
        Floor(errors, nameof(o.StartBytesPerSecond), o.StartBytesPerSecond, KeyMoveMoverOptions.FloorBytesPerSecond);
        Floor(errors, nameof(o.MinKeysPerSecond), o.MinKeysPerSecond, KeyMoveMoverOptions.FloorKeysPerSecond);
        Floor(errors, nameof(o.MinBytesPerSecond), o.MinBytesPerSecond, KeyMoveMoverOptions.FloorBytesPerSecond);
        Floor(errors, nameof(o.MaxKeysPerSecond), o.MaxKeysPerSecond, KeyMoveMoverOptions.FloorKeysPerSecond);
        Floor(errors, nameof(o.MaxBytesPerSecond), o.MaxBytesPerSecond, KeyMoveMoverOptions.FloorBytesPerSecond);
        Order(errors, nameof(o.MinKeysPerSecond), o.MinKeysPerSecond, nameof(o.StartKeysPerSecond), o.StartKeysPerSecond);
        Order(errors, nameof(o.MinBytesPerSecond), o.MinBytesPerSecond, nameof(o.StartBytesPerSecond), o.StartBytesPerSecond);
        Order(errors, nameof(o.StartKeysPerSecond), o.StartKeysPerSecond, nameof(o.MaxKeysPerSecond), o.MaxKeysPerSecond);
        Order(errors, nameof(o.StartBytesPerSecond), o.StartBytesPerSecond, nameof(o.MaxBytesPerSecond), o.MaxBytesPerSecond);
        Cap(errors, nameof(o.GlobalKeysPerSecond), o.GlobalKeysPerSecond, KeyMoveMoverOptions.FloorKeysPerSecond);
        Cap(errors, nameof(o.GlobalBytesPerSecond), o.GlobalBytesPerSecond, KeyMoveMoverOptions.FloorBytesPerSecond);
        Finite(errors, nameof(o.MaxMemoryGrowthBytesPerSecond), o.MaxMemoryGrowthBytesPerSecond);
        if (!double.IsFinite(o.IncreaseStep) || o.IncreaseStep < 0)
        {
            errors.Add($"{nameof(o.IncreaseStep)} must be finite and not negative.");
        }

        if (!double.IsFinite(o.MemoryCeilingFraction) || o.MemoryCeilingFraction <= 0 || o.MemoryCeilingFraction > 1)
        {
            errors.Add($"{nameof(o.MemoryCeilingFraction)} must be above 0 and at most 1.");
        }

        Interval(errors, nameof(o.IncreaseInterval), o.IncreaseInterval, Hour);
        Interval(errors, nameof(o.EvaluationInterval), o.EvaluationInterval, Hour);
        Interval(errors, nameof(o.HealthTimeout), o.HealthTimeout, Hour);
        Interval(errors, nameof(o.MemoryGrowthWindow), o.MemoryGrowthWindow, Hour);
        Interval(errors, nameof(o.OomBackoff), o.OomBackoff, TimeSpan.FromDays(1));
        Interval(errors, nameof(o.MoverP99Window), o.MoverP99Window, Hour);
        if (o.EvaluationInterval > TimeSpan.Zero && o.MemoryGrowthWindow < 2 * o.EvaluationInterval)
        {
            errors.Add($"{nameof(o.MemoryGrowthWindow)} must be at least twice {nameof(o.EvaluationInterval)}, or the growth rule never sees two readings.");
        }

        Positive(errors, nameof(o.CpuAmberPercent), o.CpuAmberPercent);
        Positive(errors, nameof(o.CpuRedPercent), o.CpuRedPercent);
        Order(errors, nameof(o.CpuAmberPercent), o.CpuAmberPercent, nameof(o.CpuRedPercent), o.CpuRedPercent);
        NotNegative(errors, nameof(o.ReplicationLagAmberBytes), o.ReplicationLagAmberBytes);
        NotNegative(errors, nameof(o.ReplicationLagRedBytes), o.ReplicationLagRedBytes);
        Order(errors, nameof(o.ReplicationLagAmberBytes), o.ReplicationLagAmberBytes, nameof(o.ReplicationLagRedBytes), o.ReplicationLagRedBytes);
        NotNegative(errors, nameof(o.MoverP99Amber), o.MoverP99Amber.TotalSeconds);
        NotNegative(errors, nameof(o.MoverP99Red), o.MoverP99Red.TotalSeconds);
        Order(errors, nameof(o.MoverP99Amber), o.MoverP99Amber.TotalSeconds, nameof(o.MoverP99Red), o.MoverP99Red.TotalSeconds);
        NotNegative(errors, nameof(o.TimeoutsAmber), o.TimeoutsAmber);
        NotNegative(errors, nameof(o.TimeoutsRed), o.TimeoutsRed);
        Order(errors, nameof(o.TimeoutsAmber), o.TimeoutsAmber, nameof(o.TimeoutsRed), o.TimeoutsRed);
        if (o.Windows is null)
        {
            errors.Add($"{nameof(o.Windows)} must not be null.");
        }
        else
        {
            for (var i = 0; i < o.Windows.Count; i++)
            {
                var w = o.Windows[i];
                if (w is null || w.Start < TimeSpan.Zero || w.Start >= TimeSpan.FromHours(24) || w.End < TimeSpan.Zero || w.End >= TimeSpan.FromHours(24))
                {
                    errors.Add($"{nameof(o.Windows)}[{i}] must have a Start and an End from 00:00 up to, not including, 24:00.");
                }
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Positive(List<string> errors, string name, double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            errors.Add($"{name} must be finite and above 0.");
        }
    }

    // A rate at or above its floor, so no wait is longer than one batch at the floor.
    private static void Floor(List<string> errors, string name, double value, double floor)
    {
        if (!double.IsFinite(value) || value < floor)
        {
            errors.Add($"{name} must be finite and at least {floor}.");
        }
    }

    // Zero or less is no cap; a cap is at least the floor.
    private static void Cap(List<string> errors, string name, double value, double floor)
    {
        if (!double.IsFinite(value) || (value > 0 && value < floor))
        {
            errors.Add($"{name} must be finite, and zero or less for no cap or at least {floor}.");
        }
    }

    private static void Finite(List<string> errors, string name, double value)
    {
        if (!double.IsFinite(value))
        {
            errors.Add($"{name} must be finite.");
        }
    }

    private static void NotNegative(List<string> errors, string name, double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            errors.Add($"{name} must be finite and not negative.");
        }
    }

    private static void Interval(List<string> errors, string name, TimeSpan value, TimeSpan max)
    {
        if (value <= TimeSpan.Zero || value > max)
        {
            errors.Add($"{name} must be above zero and at most {max}.");
        }
    }

    private static void Order(List<string> errors, string low, double lowValue, string high, double highValue)
    {
        if (lowValue > highValue)
        {
            errors.Add($"{low} must not be above {high}.");
        }
    }
}
