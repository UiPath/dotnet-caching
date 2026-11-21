using UiPath.Caching.Tests.PhaseB.Harness;
using UiPath.Caching.Tests.Redis;

namespace UiPath.Caching.Tests.PhaseB;

// The model of the scripts and the real scripts answer the same table of scenarios the same way and leave the same keys (lives within the time a call takes).
[Collection("RedisIntegration")]
[Trait("Category", "Integration")]
public class ModelConformanceTests(RedisContainerFixture fixture)
{
    private const long TtlTolerance = 5_000;

    // The transaction fallback creates the fence before it checks the identity and the gate, where the script refuses first; the orphan fence only holds memory for FenceTtl.
    private static readonly HashSet<string> FallbackCreatesFenceBeforeRefusing =
    [
        "CAP under another identity",
        "CAP with no gate for the cache",
        "CAP with a closed gate",
        "CAP with no record",
    ];

    public static IEnumerable<object[]> Cases => ConformanceScenarios.All.Keys.SelectMany(n => new[] { new object[] { n, true }, new object[] { n, false } });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_model_and_the_scripts_agree(string scenario, bool useScripts)
    {
        await using var env = await MoveTestEnvironment.CreateAsync(fixture, useScripts, openControl: false);
        var now = await env.ServerNowAsync();
        var model = new ModelExecutor(now);
        var redis = new RedisExecutor(env, now);

        var step = 0;
        foreach (var call in ConformanceScenarios.All[scenario])
        {
            step++;
            var expected = model.Run(call);
            var actual = await redis.RunAsync(call);
            actual.Should().Be(expected, $"step {step} ({call.Kind}) of '{scenario}'");
        }

        foreach (var role in RedisExecutor.Roles.Where(r => useScripts || r != "fence" || !FallbackCreatesFenceBeforeRefusing.Contains(scenario)))
        {
            var (content, ttl) = await redis.DumpAsync(role);
            var (modelContent, modelTtl) = Dump(model, role, now);
            content.Should().Be(modelContent, $"key '{role}' after '{scenario}'");
            (ttl == -1).Should().Be(modelTtl == -1, $"expiry of '{role}' after '{scenario}'");
            if (ttl != -1)
            {
                ttl.Should().BeInRange(modelTtl - TtlTolerance, modelTtl + TtlTolerance, $"remaining life of '{role}' after '{scenario}'");
            }
        }
    }

    private static (string Content, long Ttl) Dump(ModelExecutor model, string role, long now)
    {
        var e = model.Shard.Get(role);
        if (e is null)
        {
            return ("none", -1);
        }

        return ((e.IsHash ? "hash:" : "string:") + e.Encode(), e.ExpireAt == -1 ? -1 : e.ExpireAt - now);
    }
}
