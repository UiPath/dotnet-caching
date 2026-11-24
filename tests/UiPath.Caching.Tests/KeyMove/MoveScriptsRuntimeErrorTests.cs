using StackExchange.Redis;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Tests.KeyMove;

// A script form falls back to its transaction only when the server refused the EVAL: an error from inside a running script follows commands that may have applied, and reaches the caller as it is.
public sealed class MoveScriptsRuntimeErrorTests
{
    [Fact]
    public async Task An_error_from_inside_a_running_script_reaches_the_caller_and_leaves_scripts_on()
    {
        var database = Substitute.For<IDatabase>();
        var inside = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "ERR Error running script (call to f_1a2b): @user_script:5: @user_script: 5: -NOPERM this user has no permissions to run the 'hdel' command");
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<RedisResult>(inside));
        var scripts = new MoveScripts(database, useScripts: true, new Version(7, 0));

        var act = async () => await scripts.HashExpireAsync("k", "c", "id", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), null, HashMetadataClear.Remove);

        await act.Should().ThrowAsync<RedisServerException>();
        scripts.UsesScripts.Should().BeTrue();
        database.DidNotReceiveWithAnyArgs().CreateTransaction(default);
    }

    [Fact]
    public async Task A_refusal_of_the_eval_turns_scripts_off_and_takes_the_fallback()
    {
        var database = Substitute.For<IDatabase>();
        var refused = new RedisServerException(RedisErrorKind.None, CommandFlags.None, "NOPERM this user has no permissions to run the 'eval' command");
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>()).Returns(Task.FromException<RedisResult>(refused));
        database.CreateTransaction(Arg.Any<object?>()).Returns(Substitute.For<ITransaction>());
        var scripts = new MoveScripts(database, useScripts: true, new Version(7, 0));

        _ = await Record.ExceptionAsync(async () => await scripts.HashExpireAsync("k", "c", "id", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));

        scripts.UsesScripts.Should().BeFalse();
        database.ReceivedWithAnyArgs().CreateTransaction(default);
    }
}
