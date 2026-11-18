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

    [Theory]
    [InlineData(CacheKeyCasing.Insensitive)]
    [InlineData(CacheKeyCasing.Sensitive)]
    public void Verbatim_keeps_the_name_exactly_whatever_the_default_casing_is(CacheKeyCasing defaultCasing)
    {
        var previous = CacheKey.DefaultCasing;
        CacheKey.DefaultCasing = defaultCasing;
        try
        {
            var key = CacheKey.Verbatim("  Abc:DEF \t");

            key.Name.Should().Be("  Abc:DEF \t");
            key.Casing.Should().Be(CacheKeyCasing.Verbatim);
            key.IsNull.Should().BeFalse();
        }
        finally
        {
            CacheKey.DefaultCasing = previous;
        }
    }

    [Fact]
    public void Verbatim_constructors_and_WithName_do_not_normalize()
    {
        new CacheKey(" A ", CacheKeyCasing.Verbatim).Name.Should().Be(" A ");
        new CacheKey(" A ".AsSpan(), CacheKeyCasing.Verbatim).Name.Should().Be(" A ");
        CacheKey.Verbatim("A").WithName(" B ").Name.Should().Be(" B ");
        CacheKey.Verbatim("A").WithName(" B ").Casing.Should().Be(CacheKeyCasing.Verbatim);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Verbatim_rejects_a_name_with_no_text(string? name) =>
        FluentActions.Invoking(() => CacheKey.Verbatim(name!)).Should().Throw<ArgumentException>();

    [Fact]
    public void Verbatim_constructor_rejects_whitespace_only_and_maps_null_to_empty()
    {
        FluentActions.Invoking(() => new CacheKey("  ", CacheKeyCasing.Verbatim)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new CacheKey("  ".AsSpan(), CacheKeyCasing.Verbatim)).Should().Throw<ArgumentException>();
        new CacheKey(null, CacheKeyCasing.Verbatim).IsNull.Should().BeTrue();
    }

    [Fact]
    public void Verbatim_reaches_the_redis_key_unchanged()
    {
        var redisKey = new UiPath.Caching.Redis.PrefixRedisKeyStrategy("App", ':').GetRedisKey(CacheKey.Verbatim(" Abc:DEF "));

        redisKey.ToString().Should().Be("app: Abc:DEF ");
    }

    [Fact]
    public void Verbatim_is_not_a_valid_default_casing() =>
        FluentActions.Invoking(() => CacheKey.DefaultCasing = CacheKeyCasing.Verbatim).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void Sensitive_null_and_whitespace_still_map_to_empty()
    {
        new CacheKey(null, CacheKeyCasing.Sensitive).IsNull.Should().BeTrue();
        new CacheKey("   ", CacheKeyCasing.Sensitive).IsNull.Should().BeTrue();
    }
}
