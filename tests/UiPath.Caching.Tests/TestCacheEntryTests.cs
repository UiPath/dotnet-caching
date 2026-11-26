namespace UiPath.Caching.Tests;

public class TestCacheEntryTests
{
    private static readonly ManualTimeProvider Clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Found_defaults_to_Value_not_null_when_not_explicitly_set()
    {
        new TestCacheEntry<string?> { Value = null, Expiration = DateTimeOffset.MaxValue }.Found.Should().BeFalse();
        new TestCacheEntry<string?> { Value = "x", Expiration = DateTimeOffset.MaxValue }.Found.Should().BeTrue();
        new TestCacheEntry<string?> { Value = "x", Expiration = DateTimeOffset.MinValue }.Found.Should().BeFalse();
    }

    [Fact]
    public void Found_can_be_explicitly_overridden_to_true_for_cached_null_mocks()
    {
        var entry = new TestCacheEntry<string?> { Value = null, Found = true };
        entry.Found.Should().BeTrue();
    }

    [Fact]
    public void NewEntry_preserves_explicit_Found_override()
    {
        var entry = new TestCacheEntry<string?> { Value = null, Found = true };

        var clone = (TestCacheEntry<string?>)entry.NewEntry(Clock.GetUtcNow().AddMinutes(5));

        clone.Found.Should().BeTrue("explicit Found override must survive cloning, otherwise metadata-refresh / clone-based tests turn a cached-null hit back into a miss");
        clone.Value.Should().BeNull();
    }

    [Fact]
    public void NewEntry_without_override_uses_default_rule()
    {
        var entry = new TestCacheEntry<string?> { Value = null };

        var clone = (TestCacheEntry<string?>)entry.NewEntry(Clock.GetUtcNow().AddMinutes(5));

        clone.Found.Should().BeFalse();
    }
}
