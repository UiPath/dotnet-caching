using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

public class MoveScriptsConstructorTests
{
    [Theory]
    [InlineData("6.0.8")]
    [InlineData("5.0.14")]
    [InlineData(null)]
    public void Without_scripts_an_older_or_unknown_server_is_refused(string? version)
    {
        var act = () => new MoveScripts(Substitute.For<IDatabase>(), useScripts: false, version is null ? null : Version.Parse(version));

        act.Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("6.0.9")]
    [InlineData("7.4.0")]
    public void Without_scripts_a_server_from_6_0_9_is_accepted(string version)
    {
        var scripts = new MoveScripts(Substitute.For<IDatabase>(), useScripts: false, Version.Parse(version));

        scripts.UsesScripts.Should().BeFalse();
    }

    [Theory]
    [InlineData("6.0.8")]
    [InlineData(null)]
    public void With_scripts_any_server_version_is_accepted(string? version)
    {
        var scripts = new MoveScripts(Substitute.For<IDatabase>(), useScripts: true, version is null ? null : Version.Parse(version));

        scripts.UsesScripts.Should().BeTrue();
    }
}
