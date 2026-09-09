using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UiPath.Caching.Config;
using UiPath.Caching.Logging;

namespace UiPath.Caching.Tests.Logging;

public class KeyMaskingTests
{
    private static string Render(IKeyMaskingPolicy policy, string key, string? composed = null, Type? valueType = null) =>
        new KeyMasker(policy, KnownCacheProviderNames.Redis).Render(key, composed, valueType);

    [Fact]
    public void Nothing_is_masked_without_a_policy()
    {
        Render(NullKeyMaskingPolicy.Instance, "session:cosmin").Should().Be("session:cosmin");
    }

    [Fact]
    public void A_masked_key_keeps_three_characters()
    {
        Render(new PrefixKeyMaskingPolicy(), "cosmin").Should().Be("cos****");
    }

    [Fact]
    public void A_key_too_short_to_reveal_is_masked_whole()
    {
        Render(new PrefixKeyMaskingPolicy(), "ab").Should().Be("****");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void An_identifier_stays_readable(string key)
    {
        Render(new PrefixKeyMaskingPolicy(), key).Should().Be(key);
    }

    [Theory]
    [InlineData("session:cosmin", true)]
    [InlineData("SESSION:cosmin", true)]
    [InlineData("public:banner", false)]
    public void Only_the_listed_prefixes_are_secret(string key, bool masked)
    {
        var rendered = Render(new PrefixKeyMaskingPolicy("session:"), key);

        (rendered != key).Should().Be(masked);
    }

    [Fact]
    public void The_composed_key_keeps_everything_the_strategy_added_around_the_masked_part()
    {
        Render(new PrefixKeyMaskingPolicy(), "cosmin", "myapp:s:cosmin").Should().Be("myapp:s:cos****");
    }

    /// <summary>A suffix, a hash tag, both: nothing about the layout is assumed, only that the key is in there.</summary>
    [Theory]
    [InlineData("cosmin:v2", "cos****:v2")]
    [InlineData("{cosmin}", "{cos****}")]
    [InlineData("app:cosmin:v2", "app:cos****:v2")]
    public void The_caller_key_is_spliced_wherever_it_sits(string composed, string expected)
    {
        Render(new PrefixKeyMaskingPolicy(), "cosmin", composed).Should().Be(expected);
    }

    [Fact]
    public void Every_occurrence_of_the_caller_key_is_masked()
    {
        Render(new PrefixKeyMaskingPolicy(), "cosmin", "{cosmin}:app:cosmin").Should().Be("{cos****}:app:cos****");
    }

    /// <summary>A strategy that hashes the key leaves nothing to splice, so the whole thing goes rather than leaking.</summary>
    [Fact]
    public void A_composed_key_the_caller_key_is_not_in_is_masked_whole()
    {
        Render(new PrefixKeyMaskingPolicy(), "cosmin", "myapp:s:9f86d081").Should().Be("mya****");
    }

    [Fact]
    public void A_throwing_policy_masks_rather_than_escaping_the_log_call()
    {
        Render(new ThrowingPolicy(), "cosmin", "myapp:s:cosmin").Should().Be("myapp:s:cos****");
    }

    [Fact]
    public void The_policy_is_told_what_the_cache_holds_and_which_tier_asked()
    {
        var policy = new RecordingPolicy();

        Render(policy, "cosmin", composed: null, valueType: typeof(int));

        policy.Last.Key.Should().Be("cosmin");
        policy.Last.ValueType.Should().Be<int>();
        policy.Last.CacheName.Should().Be(KnownCacheProviderNames.Redis);
    }

    [Fact]
    public void A_container_without_masking_resolves_the_null_policy()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.AddMemory(_ => { }));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IKeyMaskingPolicy>().Should().BeSameAs(NullKeyMaskingPolicy.Instance);
    }

    [Fact]
    public void AddKeyMasking_replaces_the_null_policy()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.AddMemory(_ => { }).AddKeyMasking("session:"));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IKeyMaskingPolicy>().Should().BeOfType<PrefixKeyMaskingPolicy>();
    }

    [Fact]
    public void AddKeyMasking_takes_a_policy_of_your_own()
    {
        var services = new ServiceCollection();
        services.AddCaching(b => b.AddMemory(_ => { }).AddKeyMasking<RecordingPolicy>());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IKeyMaskingPolicy>().Should().BeOfType<RecordingPolicy>();
    }

    private sealed class ThrowingPolicy : IKeyMaskingPolicy
    {
        public bool ShouldMask(in MaskingContext context) => throw new InvalidOperationException("policy is broken");
    }

    private sealed class RecordingPolicy : IKeyMaskingPolicy
    {
        public MaskingContext Last { get; private set; }

        public bool ShouldMask(in MaskingContext context)
        {
            Last = context;
            return true;
        }
    }
}
