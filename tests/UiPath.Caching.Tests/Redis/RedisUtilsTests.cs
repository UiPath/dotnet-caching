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

    // Only a refusal of the EVAL itself proves that no command of the script ran. The forms are those the servers raise when the ACL denies a command, with the user name in it since Redis 7.2 and in Valkey and without it before, and when EVAL is not a command at all.
    [Theory]
    [InlineData("NOPERM this user has no permissions to run the 'eval' command or its subcommand")]
    [InlineData("NOPERM this user has no permissions to run the 'eval' command")]
    [InlineData("NOPERM this user has no permissions to run the 'evalsha' command")]
    [InlineData("NOPERM User default has no permissions to run the 'eval' command")]
    [InlineData("NOPERM User service-account has no permissions to run the 'evalsha' command")]
    [InlineData("ERR unknown command 'EVAL', with args beginning with: ")]
    [InlineData("ERR unknown command `EVAL`, with args beginning with: ")]
    // A user name cannot be told from the structure around it: these are refusals of the EVAL for users whose names hold the words of a script error.
    [InlineData("NOPERM User user_script has no permissions to run the 'eval' command")]
    [InlineData("NOPERM User script: abc has no permissions to run the 'eval' command")]
    [InlineData("NOPERM User my_user_script:7 has no permissions to run the 'evalsha' command")]
    public void A_refusal_of_the_eval_command_proves_the_script_never_ran(string message) =>
        RedisUtils.ScriptNeverRan(new RedisServerException(RedisErrorKind.None, CommandFlags.None, message)).Should().BeTrue();

    [Fact]
    public void An_eval_the_client_disabled_proves_the_script_never_ran() =>
        RedisUtils.ScriptNeverRan(new RedisCommandException("This operation has been disabled in the command-map and cannot be used: EVAL")).Should().BeTrue();

    // What a running script raises: the error of its command with the script named at the end (", script: <sha1>, on @user_script:<line>.", Redis 7 and Valkey), or wrapped as "Error running script" (Redis 6). A user whose name holds the words of the suffix does not change that.
    [Theory]
    [InlineData("ERR ACL failure in script: User default has no permissions to run the 'pexpireat' command script: 0123456789abcdef0123456789abcdef01234567, on @user_script:3.")]
    [InlineData("ERR ACL failure in script: No permissions to access a key script: 0123456789abcdef0123456789abcdef01234567, on @user_script:8.")]
    [InlineData("NOPERM User default has no permissions to run the 'pexpireat' command script: 0123456789abcdef0123456789abcdef01234567, on @user_script:3.")]
    [InlineData("NOPERM User user_script has no permissions to run the 'pexpireat' command script: 0123456789abcdef0123456789abcdef01234567, on @user_script:3.")]
    [InlineData("NOPERM User script: abc has no permissions to run the 'hset' command script: 0123456789abcdef0123456789abcdef01234567, on @user_script:2.")]
    [InlineData("NOPERM User default has no permissions to run the 'evalsha' command script: 0123456789abcdef0123456789abcdef01234567, on @user_script:1.")]
    [InlineData("ERR Error running script (call to f_0123456789abcdef0123456789abcdef01234567): @user_script:3: @user_script: 3: -NOPERM this user has no permissions to run the 'pexpireat' command")]
    [InlineData("ERR Error running script (call to f_0123456789abcdef0123456789abcdef01234567): @user_script:3: WRONGTYPE Operation against a key holding the wrong kind of value")]
    [InlineData("ERR This Redis command is not allowed from script script: 0123456789abcdef0123456789abcdef01234567, on @user_script:8.")]
    [InlineData("NOPERM this user has no permissions to access one of the keys used as arguments script: 0123456789abcdef0123456789abcdef01234567, on @user_script:8.")]
    public void An_error_from_inside_a_running_script_does_not_prove_that_nothing_ran(string message) =>
        RedisUtils.ScriptNeverRan(new RedisServerException(RedisErrorKind.None, CommandFlags.None, message)).Should().BeFalse();

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
