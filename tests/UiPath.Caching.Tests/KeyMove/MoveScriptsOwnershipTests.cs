using StackExchange.Redis;
using UiPath.Caching.KeyMove;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.KeyMove;

// The transaction fallback asks whether its caller still owns the work before every attempt, so a retry after a lost lease is never sent.
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class MoveScriptsOwnershipTests(RedisContainerFixture fixture)
{
    [Theory]
    [InlineData("cmpdel")]
    [InlineData("pub")]
    [InlineData("cap")]
    public async Task A_takeover_between_two_fallback_attempts_stops_the_second_attempt_from_being_sent(string operation)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts: false);
        var src = env.Make("src");
        await env.Db.StringSetAsync(src, "v");
        var attempts = 0;

        // Every transaction aborts with its conditions still holding, and every capture batch finds its fence gone: the operation asks for another attempt.
        var db = InterceptingProxy.Wrap(env.Db, (method, args) =>
        {
            if (method.Name == nameof(IDatabase.CreateTransaction))
            {
                Interlocked.Increment(ref attempts);
                var real = (ITransaction)method.Invoke(env.Db, args)!;
                return InterceptingProxy.Wrap(real, (m, _) => m.Name == nameof(ITransaction.ExecuteAsync) ? Task.FromResult(false) : InterceptingProxy.Proceed);
            }

            if (method.Name == nameof(IDatabase.CreateBatch))
            {
                Interlocked.Increment(ref attempts);
                var real = (IBatch)method.Invoke(env.Db, args)!;
                return InterceptingProxy.Wrap(real, (m, a) => operation == "cap" && m.Name == nameof(IBatch.StringGetAsync) && a[0] is RedisKey k && k == env.Fence ? Task.FromResult(RedisValue.Null) : InterceptingProxy.Proceed);
            }

            return InterceptingProxy.Proceed;
        });
        var scripts = new MoveScripts(db, useScripts: false, env.ServerVersion);
        var capture = await env.CaptureAsync("n1");
        attempts = 0;
        var asked = 0;

        // Owned for the first attempt, lost before the second.
        bool Owned() => Interlocked.Increment(ref asked) == 1;
        Func<Task> act = operation switch
        {
            "cmpdel" => () => scripts.CompareDeleteAsync(src, env.Ctl, MoveTestEnvironment.Id, "v", Owned),
            "pub" => () => scripts.PublishAsync(env.Keys, MoveTestEnvironment.Id, "x", MoveTestEnvironment.PublicationFor(capture, "n2"), Owned),
            _ => () => scripts.CaptureAsync(env.Keys, MoveTestEnvironment.Id, MoveTestEnvironment.Cid, "n3", 30_000, owned: Owned),
        };

        await act.Should().ThrowAsync<OwnershipLostException>();

        asked.Should().Be(2);
        attempts.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(operation == "pub" ? 2 : 1, "no attempt is sent after the lease was lost");
        (await env.Db.StringGetAsync(src)).Should().Be("v");
        (await env.Db.KeyExistsAsync(env.Dst)).Should().BeFalse();
    }
}
