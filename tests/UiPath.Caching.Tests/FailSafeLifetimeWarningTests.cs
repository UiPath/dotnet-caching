using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Tests;

/// <summary>A policy that enables fail-safe on a cache with no size limit gets one startup warning, which names it.</summary>
public class FailSafeLifetimeWarningTests
{
    private static readonly CachePolicy FailSafe = new() { FailSafeEnabled = true };

    private readonly WarningLogger _logger = new();

    [Fact]
    public void A_fail_safe_policy_without_a_size_limit_logs_one_warning_naming_it_and_its_stale_age()
    {
        Log(null, null, ("routes", new CachePolicy { FailSafeEnabled = true, FailSafeMaxStaleAge = TimeSpan.FromHours(2) }), ("plain", new CachePolicy()));

        _logger.Warnings.Should().ContainSingle().Which.Should().Contain("routes").And.Contain("FailSafeMaxStaleAge (02:00:00)").And.Contain("InMemoryRedis SizeLimit is unset");
    }

    [Fact]
    public void The_default_stale_age_is_one_day()
    {
        Log(null, null, ("routes", FailSafe));

        _logger.Warnings.Should().ContainSingle().Which.Should().Contain("(1.00:00:00)");
    }

    [Fact]
    public void A_fail_safe_default_policy_warns_for_itself_and_each_policy_that_inherits_it()
    {
        Log(null, FailSafe, ("a", FailSafe), ("b", FailSafe));

        _logger.Warnings.Should().HaveCount(3);
        _logger.Warnings.Should().ContainSingle(w => w.Contains(FailSafeLifetimeWarning.DefaultPolicyName));
    }

    [Fact]
    public void A_local_lifetime_cap_does_not_bound_the_kept_values()
    {
        Log(null, null, ("capped", new CachePolicy { FailSafeEnabled = true, LocalExpiration = TimeSpan.FromMinutes(5) }));

        _logger.Warnings.Should().ContainSingle("a kept value outlives its local copy by the stale age, whatever caps the local tier").Which.Should().Contain("capped");
    }

    [Fact]
    public void A_size_limit_silences_every_policy()
    {
        Log(1000, FailSafe, ("routes", FailSafe));

        _logger.Warnings.Should().BeEmpty("the kept values are limited to the same size");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_size_limit_the_cache_ignores_is_no_limit(long sizeLimit)
    {
        Log(sizeLimit, null, ("routes", FailSafe));

        _logger.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void A_policy_without_fail_safe_logs_nothing()
    {
        Log(null, new CachePolicy(), ("off", new CachePolicy { FailSafeEnabled = false }));

        _logger.Warnings.Should().BeEmpty();
    }

    private void Log(long? sizeLimit, CachePolicy? defaultPolicy, params (string Name, CachePolicy Policy)[] named)
    {
        var factory = Substitute.For<ICachePolicyFactory>();
        factory.Default.Returns(defaultPolicy);
        factory.Keys.Returns(named.Select(p => p.Name).ToArray());
        foreach (var (name, policy) in named)
        {
            factory.Resolve(name).Returns(policy);
        }

        FailSafeLifetimeWarning.Log(_logger, KnownCacheProviderNames.InMemoryRedis, factory, sizeLimit);
    }

    internal sealed class WarningLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
