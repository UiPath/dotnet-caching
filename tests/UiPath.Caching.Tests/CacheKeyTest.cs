using System.Globalization;

namespace UiPath.Caching.Tests;

public class CacheKeyTest
{
    private readonly IFixture _fixture = AutoFixtureCreator.NSubstitute();

    [Fact]
    public void AllOperations()
    {
        new CacheKey(null).IsNull.Should().BeTrue();
        new CacheKey().IsNull.Should().BeTrue();
        new CacheKey(string.Empty).IsNull.Should().BeTrue();
        new CacheKey("         ").IsNull.Should().BeTrue();
        var value = _fixture.Create<string>().ToUpperInvariant();
        Assert.True(new CacheKey(value) == new CacheKey(value.ToLowerInvariant()));
        Assert.True(new CacheKey(value) == value.ToLowerInvariant());
        Assert.True(new CacheKey(value) != new CacheKey(value.ToLowerInvariant() + _fixture.Create<string>()));
        Assert.True(new CacheKey(value) != value.ToLowerInvariant() + _fixture.Create<string>());
        Assert.True(new CacheKey(value).GetHashCode() != 0);
        Assert.True(new CacheKey(value).GetHashCode() == new CacheKey(value.ToLowerInvariant()).GetHashCode());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(42)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Implicit_cast_from_int(int value)
    {
        CacheKey key = value;
        key.Name.Should().Be(value.ToString(CultureInfo.InvariantCulture));
        key.Should().Be(new CacheKey(value.ToString(CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(9_999_999_999L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Implicit_cast_from_long(long value)
    {
        CacheKey key = value;
        key.Name.Should().Be(value.ToString(CultureInfo.InvariantCulture));
        key.Should().Be(new CacheKey(value.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Implicit_cast_from_Guid_uses_lowercased_D_format()
    {
        var guid = Guid.NewGuid();
        CacheKey key = guid;
        key.Name.Should().Be(guid.ToString().ToLowerInvariant());
        key.Name.Length.Should().Be(36);
    }

    [Fact]
    public void Implicit_cast_from_Guid_empty()
    {
        CacheKey key = Guid.Empty;
        key.Name.Should().Be("00000000-0000-0000-0000-000000000000");
    }

    [Fact]
    public void Equals_int_matches_cast_round_trip()
    {
        CacheKey key = 42;
        key.Equals(42).Should().BeTrue();
        key.Equals(43).Should().BeFalse();
        (key == 42).Should().BeTrue();
        (key == 43).Should().BeFalse();
    }

    [Fact]
    public void Equals_long_matches_cast_round_trip()
    {
        CacheKey key = 9_999_999_999L;
        key.Equals(9_999_999_999L).Should().BeTrue();
        key.Equals(1L).Should().BeFalse();
        (key == 9_999_999_999L).Should().BeTrue();
    }

    [Fact]
    public void Equals_Guid_matches_cast_round_trip()
    {
        var guid = Guid.NewGuid();
        CacheKey key = guid;
        key.Equals(guid).Should().BeTrue();
        key.Equals(Guid.NewGuid()).Should().BeFalse();
        (key == guid).Should().BeTrue();
    }

    [Fact]
    public void Equals_object_returns_false_for_non_CacheKey_types()
    {
        CacheKey key = 42;
        key.Equals((object)42).Should().BeFalse();
        key.Equals((object)42L).Should().BeFalse();
        key.Equals((object)Guid.NewGuid()).Should().BeFalse();
        key.Equals((object)"foo").Should().BeFalse();
        key.Equals((object?)null).Should().BeFalse();
    }

    [Fact]
    public void Numeric_casts_pin_to_invariant_negative_sign()
    {
        var custom = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        custom.NumberFormat.NegativeSign = "~";

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = custom;
            (-42).ToString().Should().Be("~42");
            (-42L).ToString().Should().Be("~42");

            CacheKey intKey = -42;
            CacheKey longKey = -42L;
            intKey.Name.Should().Be("-42");
            longKey.Name.Should().Be("-42");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
