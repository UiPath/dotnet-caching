using StackExchange.Redis;
using UiPath.Caching.Redis;
using Xunit;

namespace UiPath.Caching.Tests.Redis;

public sealed class RedisUtilsTests
{
    [Theory]
    [InlineData("NOPERM this user has no permissions to run the 'eval' command")]
    [InlineData("ERR unknown command 'EVAL', with args beginning with: ")]
    [InlineData("ERR ACL failure in script: No permissions to access a channel script: f_1a2b, on @user_script:8.")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:3: @user_script: 3: -NOPERM this user has no permissions to run the 'xadd' command")]
    [InlineData("ERR This Redis command is not allowed from script script: f_1a2b, on @user_script:8.")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:8: @user_script: 8: This Redis command is not allowed from scripts")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:8: @user_script: 8: The user executing the script can't run this command or subcommand")]
    public void A_script_refused_for_good_is_recognized(string message) =>
        RedisUtils.RefusesScript(new RedisServerException(RedisErrorKind.None, CommandFlags.None, message)).Should().BeTrue();

    [Fact]
    public void A_script_the_client_disabled_is_recognized() =>
        RedisUtils.RefusesScript(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: EVAL")).Should().BeTrue();

    // Redis 6 wraps every error raised in a script as "ERR Error running script", so the wrapper alone says nothing.
    [Theory]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:3: WRONGTYPE Operation against a key holding the wrong kind of value")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:3: @user_script: 3: -READONLY You can't write against a read only replica.")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:3: @user_script: 3: -OOM command not allowed when used memory > 'maxmemory'.")]
    [InlineData("ERR Error running script (call to f_1a2b): @user_script:3: @user_script: 3: -MISCONF Redis is configured to save RDB snapshots, but it's currently unable to persist to disk.")]
    [InlineData("BUSY Redis is busy running a script. You can only call SCRIPT KILL or SHUTDOWN NOSAVE.")]
    [InlineData("LOADING Redis is loading the dataset in memory")]
    [InlineData("OOM command not allowed when used memory > 'maxmemory'. script: f_1a2b, on @user_script:3.")]
    public void A_script_that_failed_for_a_passing_reason_is_not_taken_for_a_refusal(string message) =>
        RedisUtils.RefusesScript(new RedisServerException(RedisErrorKind.None, CommandFlags.None, message)).Should().BeFalse();
}
