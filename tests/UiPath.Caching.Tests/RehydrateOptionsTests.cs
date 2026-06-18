namespace UiPath.Caching.Tests;

public class RehydrateOptionsTests
{
    [Fact]
    public void Defaults_match_spec()
    {
        var options = new RehydrateOptions();

        options.Threshold.Should().Be(0.75);
        options.BaseCooldown.Should().Be(TimeSpan.FromSeconds(5));
        options.MaxCooldown.Should().Be(TimeSpan.FromMinutes(5));
        options.TimeoutFraction.Should().Be(0.5);
        options.Name.Should().BeNull();
    }

    [Fact]
    public void Fields_carry_init_values()
    {
        var options = new RehydrateOptions
        {
            Threshold = 0.5,
            BaseCooldown = TimeSpan.FromSeconds(1),
            MaxCooldown = TimeSpan.FromMinutes(1),
            TimeoutFraction = 0.25,
            Name = "test-profile",
        };

        options.Threshold.Should().Be(0.5);
        options.BaseCooldown.Should().Be(TimeSpan.FromSeconds(1));
        options.MaxCooldown.Should().Be(TimeSpan.FromMinutes(1));
        options.TimeoutFraction.Should().Be(0.25);
        options.Name.Should().Be("test-profile");
    }
}
