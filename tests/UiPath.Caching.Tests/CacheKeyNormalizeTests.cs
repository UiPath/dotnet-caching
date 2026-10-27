namespace UiPath.Caching.Tests;

/// <summary>What key normalization writes: trimmed, and lowercased unless the casing is sensitive. A name that needs neither is copied as it is.</summary>
public class CacheKeyNormalizeTests
{
    public static TheoryData<string> Names => new()
    {
        "user:42",
        "tenant:7f3a9c1e-22b4:user:42",
        "User:42",
        "USER:42",
        "user:42X",
        "  user:42  ",
        "\tUser:42\n",
        "İstanbul",
        "ΣΊΣΥΦΟΣ",
        "user:ß",
        "naïve",
        "user:é",
        "ÀB",
        "a",
        " ",
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Insensitive_normalization_is_the_trimmed_invariant_lowercase(string name)
    {
        new CacheKey(name, CacheKeyCasing.Insensitive).Name.Should().Be(name.Trim().ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Sensitive_normalization_only_trims(string name)
    {
        new CacheKey(name, CacheKeyCasing.Sensitive).Name.Should().Be(name.Trim());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void A_composed_span_key_matches_the_key_path(string name)
    {
        var strategy = new PrefixCacheKeyStrategy("App");
        Span<char> destination = stackalloc char[SpanKey.MaxLength];

        var composed = SpanKey.TryCompose<string>(strategy, name, destination, out var written, TestContext.Current.CancellationToken);

        var expected = strategy.GetCacheKey<string>(new CacheKey(name)).Name;
        if (name.Trim().Length == 0)
        {
            composed.Should().BeFalse();
            return;
        }

        composed.Should().BeTrue();
        destination[..written].ToString().Should().Be(expected);
    }
}
