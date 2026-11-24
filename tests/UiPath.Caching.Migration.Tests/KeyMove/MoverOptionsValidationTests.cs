using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace UiPath.Caching.Migration.Tests.KeyMove;

// The options of the mover are checked at startup and at each reload; each rule has a case that breaks it and the defaults pass.
public sealed class MoverOptionsValidationTests
{
    private static readonly KeyMoveMoverOptionsValidator Validator = new();

    private static readonly (string Expected, Action<KeyMoveMoverOptions> Break)[] Cases =
    [
        ("StartKeysPerSecond", o => o.StartKeysPerSecond = 0),
        ("StartKeysPerSecond", o => o.StartKeysPerSecond = -1),
        ("StartKeysPerSecond", o => o.StartKeysPerSecond = double.NaN),
        ("StartBytesPerSecond", o => o.StartBytesPerSecond = double.PositiveInfinity),
        ("MinKeysPerSecond", o => o.MinKeysPerSecond = 0),
        ("MinBytesPerSecond", o => o.MinBytesPerSecond = -5),
        ("MaxKeysPerSecond", o => o.MaxKeysPerSecond = double.PositiveInfinity),
        ("MaxBytesPerSecond", o => o.MaxBytesPerSecond = 0),
        ("MinKeysPerSecond must not be above StartKeysPerSecond", o => o.MinKeysPerSecond = 101),
        ("MinBytesPerSecond must not be above StartBytesPerSecond", o => o.MinBytesPerSecond = 2 * 1024 * 1024),
        ("StartKeysPerSecond must not be above MaxKeysPerSecond", o => o.MaxKeysPerSecond = 50),
        ("GlobalKeysPerSecond", o => o.GlobalKeysPerSecond = double.NaN),
        ("GlobalBytesPerSecond", o => o.GlobalBytesPerSecond = double.PositiveInfinity),
        ("IncreaseStep", o => o.IncreaseStep = -0.1),
        ("IncreaseStep", o => o.IncreaseStep = double.PositiveInfinity),
        ("MemoryCeilingFraction", o => o.MemoryCeilingFraction = 0),
        ("MemoryCeilingFraction", o => o.MemoryCeilingFraction = 1.01),
        ("IncreaseInterval", o => o.IncreaseInterval = TimeSpan.Zero),
        ("EvaluationInterval", o => o.EvaluationInterval = TimeSpan.Zero),
        ("EvaluationInterval", o => o.EvaluationInterval = TimeSpan.FromSeconds(-1)),
        ("HealthTimeout", o => o.HealthTimeout = TimeSpan.Zero),
        ("OomBackoff", o => o.OomBackoff = TimeSpan.Zero),
        ("MoverP99Window", o => o.MoverP99Window = TimeSpan.Zero),
        ("MemoryGrowthWindow", o => o.MemoryGrowthWindow = TimeSpan.Zero),
        ("MemoryGrowthWindow must be at least twice", o => o.MemoryGrowthWindow = TimeSpan.FromSeconds(19)),
        ("CpuAmberPercent must not be above CpuRedPercent", o => o.CpuAmberPercent = 95),
        ("ReplicationLagAmberBytes must not be above", o => o.ReplicationLagAmberBytes = long.MaxValue),
        ("MoverP99Amber must not be above", o => o.MoverP99Amber = TimeSpan.FromMinutes(1)),
        ("TimeoutsAmber must not be above", o => o.TimeoutsAmber = 6),
        ("Windows[0]", o => o.Windows = [new MoveWindow { Start = TimeSpan.FromHours(24), End = TimeSpan.FromHours(1) }]),
        ("Windows[0]", o => o.Windows = [new MoveWindow { Start = TimeSpan.FromHours(1), End = TimeSpan.FromHours(30) }]),
        ("Windows[1]", o => o.Windows = [new MoveWindow { Start = TimeSpan.FromHours(1), End = TimeSpan.FromHours(2) }, new MoveWindow { Start = TimeSpan.FromHours(-1), End = TimeSpan.FromHours(2) }]),
        ("StartBytesPerSecond must not be above MaxBytesPerSecond", o => o.MaxBytesPerSecond = 500_000),
        ("CpuAmberPercent", o => o.CpuAmberPercent = 0),
        ("CpuAmberPercent", o => o.CpuAmberPercent = double.NaN),
        ("CpuRedPercent", o => o.CpuRedPercent = double.NaN),
        ("CpuRedPercent", o => o.CpuRedPercent = double.PositiveInfinity),
        ("ReplicationLagAmberBytes must be finite and not negative", o => o.ReplicationLagAmberBytes = -1),
        ("ReplicationLagRedBytes must be finite and not negative", o => (o.ReplicationLagAmberBytes, o.ReplicationLagRedBytes) = (-5, -1)),
        ("MoverP99Amber must be finite and not negative", o => (o.MoverP99Amber, o.MoverP99Red) = (TimeSpan.FromSeconds(-2), TimeSpan.FromSeconds(1))),
        ("MoverP99Red must be finite and not negative", o => (o.MoverP99Amber, o.MoverP99Red) = (TimeSpan.FromSeconds(-3), TimeSpan.FromSeconds(-1))),
        ("TimeoutsAmber must be finite and not negative", o => o.TimeoutsAmber = -1),
        ("TimeoutsRed must be finite and not negative", o => (o.TimeoutsAmber, o.TimeoutsRed) = (-5, -1)),
        ("MaxMemoryGrowthBytesPerSecond", o => o.MaxMemoryGrowthBytesPerSecond = double.NaN),
        ("HealthTimeout must be above zero and at most", o => o.HealthTimeout = TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)),
        ("IncreaseInterval must be above zero and at most", o => o.IncreaseInterval = TimeSpan.FromHours(2)),
        ("EvaluationInterval must be above zero and at most", o => o.EvaluationInterval = TimeSpan.FromHours(2)),
        ("MemoryGrowthWindow must be above zero and at most", o => o.MemoryGrowthWindow = TimeSpan.FromHours(2)),
        ("MoverP99Window must be above zero and at most", o => o.MoverP99Window = TimeSpan.FromHours(2)),
        ("OomBackoff must be above zero and at most", o => o.OomBackoff = TimeSpan.FromDays(2)),
        ("OomBackoff must be above zero and at most", o => o.OomBackoff = TimeSpan.MaxValue),
        ("Windows must not be null", o => o.Windows = null!),
        ("MinKeysPerSecond must be finite and at least 0.1", o => o.MinKeysPerSecond = 0.09),
        ("MinBytesPerSecond must be finite and at least 1024", o => o.MinBytesPerSecond = 1023),
        ("StartKeysPerSecond must be finite and at least 0.1", o => (o.StartKeysPerSecond, o.MinKeysPerSecond) = (0.05, 0.05)),
        ("StartBytesPerSecond must be finite and at least 1024", o => (o.StartBytesPerSecond, o.MinBytesPerSecond) = (512, 512)),
        ("MaxKeysPerSecond must be finite and at least 0.1", o => (o.StartKeysPerSecond, o.MinKeysPerSecond, o.MaxKeysPerSecond) = (0.01, 0.01, 0.01)),
        ("MaxBytesPerSecond must be finite and at least 1024", o => (o.StartBytesPerSecond, o.MinBytesPerSecond, o.MaxBytesPerSecond) = (1, 1, 1)),
        ("GlobalKeysPerSecond must be finite, and zero or less for no cap or at least 0.1", o => o.GlobalKeysPerSecond = 0.05),
        ("GlobalBytesPerSecond must be finite, and zero or less for no cap or at least 1024", o => o.GlobalBytesPerSecond = 100),
    ];

    public static TheoryData<int, string> Breaks
    {
        get
        {
            var data = new TheoryData<int, string>();
            for (var i = 0; i < Cases.Length; i++)
            {
                data.Add(i, Cases[i].Expected);
            }

            return data;
        }
    }

    [Fact]
    public void The_defaults_and_the_edges_of_every_range_are_valid()
    {
        Validator.Validate(null, new KeyMoveMoverOptions()).Succeeded.Should().BeTrue();
        Validator.Validate(
            null,
            new KeyMoveMoverOptions
            {
                MemoryCeilingFraction = 1,
                IncreaseStep = 0,
                MinKeysPerSecond = 100,
                MaxKeysPerSecond = 100,
                GlobalKeysPerSecond = 0,
                GlobalBytesPerSecond = -1,
                EvaluationInterval = TimeSpan.FromSeconds(30),
                MemoryGrowthWindow = TimeSpan.FromSeconds(60),
                Windows = [new MoveWindow { Start = TimeSpan.Zero, End = new TimeSpan(23, 59, 59) }],
            }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void The_floors_are_valid_rates_for_every_rate_and_for_the_cap()
    {
        var floors = new KeyMoveMoverOptions
        {
            StartKeysPerSecond = 0.1,
            MinKeysPerSecond = 0.1,
            MaxKeysPerSecond = 0.1,
            StartBytesPerSecond = 1024,
            MinBytesPerSecond = 1024,
            MaxBytesPerSecond = 1024,
            GlobalKeysPerSecond = 0.1,
            GlobalBytesPerSecond = 1024,
        };

        Validator.Validate(null, floors).Succeeded.Should().BeTrue();
        KeyMoveMoverOptions.FloorKeysPerSecond.Should().Be(0.1);
        KeyMoveMoverOptions.FloorBytesPerSecond.Should().Be(1024);
    }

    [Theory]
    [MemberData(nameof(Breaks))]
    public void A_rule_that_is_broken_fails_with_its_name(int index, string expected)
    {
        var options = new KeyMoveMoverOptions { EvaluationInterval = TimeSpan.FromSeconds(10) };
        Cases[index].Break(options);

        var result = Validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_options_are_checked_on_start_and_on_reload_through_the_registration()
    {
        var services = new ServiceCollection();
        services.AddMoverRate();
        services.Configure<KeyMoveMoverOptions>(o => o.StartKeysPerSecond = 0);
        using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptionsMonitor<KeyMoveMoverOptions>>().CurrentValue;

        read.Should().Throw<OptionsValidationException>().WithMessage("*StartKeysPerSecond*");
        provider.GetServices<IValidateOptions<KeyMoveMoverOptions>>().Should().ContainSingle().Which.Should().BeOfType<KeyMoveMoverOptionsValidator>();
    }
}
