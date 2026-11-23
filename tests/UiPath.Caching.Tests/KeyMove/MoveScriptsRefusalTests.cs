using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MoveScriptsRefusalTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData("NOPERM this user has no permissions to run the 'evalsha' command")]
    [InlineData("ERR unknown command 'EVALSHA', with args beginning with:")]
    public async Task A_refused_script_switches_to_the_fallback_and_tells_the_callback_once(string failure)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true);
        var told = new List<Exception>();
        var scripts = new MoveScripts(Refusing(env.Db, failure), useScripts: true, env.ServerVersion, told.Add);

        var first = await scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x");
        var second = await scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "OPEN:y");

        first.Should().Be(1);
        second.Should().Be(1);
        told.Should().ContainSingle();
        scripts.UsesScripts.Should().BeFalse();
        (await env.Db.HashGetAsync(env.Ctl, "c:" + MoveTestEnvironment.Cid)).Should().Be("OPEN:y");
    }

    [Fact]
    public async Task Concurrent_refused_calls_tell_the_callback_once()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true);
        var told = 0;
        var scripts = new MoveScripts(Refusing(env.Db, "NOPERM no"), useScripts: true, env.ServerVersion, _ => Interlocked.Increment(ref told));

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x"))));

        results.Should().OnlyContain(r => r == 1);
        told.Should().Be(1);
    }

    [Fact]
    public async Task A_refused_script_on_a_server_older_than_6_0_9_throws_and_writes_nothing()
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: true);
        var told = 0;
        var scripts = new MoveScripts(Refusing(env.Db, "NOPERM no"), useScripts: true, new Version(6, 0, 8), _ => told++);
        var before = await env.Db.HashGetAsync(env.Ctl, "c:" + MoveTestEnvironment.Cid);

        var act = () => scripts.GateAsync(env.Ctl, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "CLOSED:x");

        await act.Should().ThrowAsync<NotSupportedException>();
        told.Should().Be(1);
        scripts.UsesScripts.Should().BeFalse();
        (await env.Db.HashGetAsync(env.Ctl, "c:" + MoveTestEnvironment.Cid)).Should().Be(before);
    }

    private static IDatabase Refusing(IDatabase db, string failure) =>
        InterceptingProxy.Wrap(db, (method, _) =>
            method.Name == nameof(IDatabase.ScriptEvaluateAsync)
                ? Task.FromException<RedisResult>(new RedisServerException(RedisErrorKind.None, CommandFlags.None, failure))
                : InterceptingProxy.Proceed);
}
