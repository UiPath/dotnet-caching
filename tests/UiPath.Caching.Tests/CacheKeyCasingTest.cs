namespace UiPath.Caching.Tests;

public class CacheKeyCasingTest
{
    [Fact]
    public void Insensitive_ctor_trims_and_lowercases()
    {
        var key = new CacheKey("  AbC  ", CacheKeyCasing.Insensitive);
        key.Name.Should().Be("abc");
        key.Casing.Should().Be(CacheKeyCasing.Insensitive);
    }

    [Fact]
    public void Sensitive_ctor_trims_only()
    {
        var key = new CacheKey("  AbC  ", CacheKeyCasing.Sensitive);
        key.Name.Should().Be("AbC");
        key.Casing.Should().Be(CacheKeyCasing.Sensitive);
    }

    [Fact]
    public void Sensitive_keys_with_different_case_are_not_equal()
    {
        var upper = new CacheKey("AbC", CacheKeyCasing.Sensitive);
        var lower = new CacheKey("abc", CacheKeyCasing.Sensitive);
        upper.Should().NotBe(lower);
        (upper != lower).Should().BeTrue();
    }

    [Fact]
    public void Equality_ignores_casing_mode_when_names_match()
    {
        var viaSensitive = new CacheKey("abc", CacheKeyCasing.Sensitive);
        var viaInsensitive = new CacheKey("ABC", CacheKeyCasing.Insensitive);
        viaSensitive.Should().Be(viaInsensitive);
        viaSensitive.GetHashCode().Should().Be(viaInsensitive.GetHashCode());
    }

    [Fact]
    public void WithName_preserves_casing()
    {
        var key = new CacheKey("AbC", CacheKeyCasing.Sensitive);
        var derived = key.WithName("prefix:" + key.Name);
        derived.Name.Should().Be("prefix:AbC");
        derived.Casing.Should().Be(CacheKeyCasing.Sensitive);
    }

    [Fact]
    public void Default_struct_is_insensitive_and_null()
    {
        default(CacheKey).Casing.Should().Be(CacheKeyCasing.Insensitive);
        default(CacheKey).IsNull.Should().BeTrue();
    }

    [Fact]
    public void Sensitive_null_and_whitespace_still_map_to_empty()
    {
        new CacheKey(null, CacheKeyCasing.Sensitive).IsNull.Should().BeTrue();
        new CacheKey("   ", CacheKeyCasing.Sensitive).IsNull.Should().BeTrue();
    }
}
