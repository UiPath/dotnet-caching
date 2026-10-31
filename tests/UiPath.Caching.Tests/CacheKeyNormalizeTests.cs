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

    public static TheoryData<string> Texts => new()
    {
        string.Empty,
        "a",
        "user:42",
        "User:42",
        "user:42Z",
        "@`[{",
        "user:é",
        "",
        "",
        "A",
        "İstanbul",
        new string('a', 255),
        new string('a', 256),
        new string('a', 257) + "B",
        new string('a', 300),
        new string('a', 200) + "É" + new string('b', 200),
        "tenant:7f3a9c1e-22b4-4f0a-9d57-0a1c2e3f4a51:6d1f0c52-3b0e-4f0a-9d57-0a1c2e3f4a51",
        "tenant:7f3a9c1e-22b4-4f0a-9d57-0a1c2e3f4a51:6D1F0C52-3b0e-4f0a-9d57-0a1c2e3f4a51",
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

    [Theory]
    [MemberData(nameof(Texts))]
    public void Normalization_matches_the_character_loop_it_replaced(string text)
    {
        var trimmed = text.Trim();
        var expected = IsLowerAsciiByLoop(trimmed) ? trimmed : trimmed.ToLowerInvariant();

        // The span constructor normalizes through the same check, on the stack up to its limit and rented beyond.
        new CacheKey(text.AsSpan(), CacheKeyCasing.Insensitive).Name.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void A_default_strategy_composition_is_the_normalized_text(string text)
    {
        Span<char> destination = stackalloc char[SpanKey.MaxLength];

        var composed = SpanKey.TryCompose<string>(new DefaultCacheKeyStrategy(), text, destination, out var written, TestContext.Current.CancellationToken);

        var expected = text.Trim().ToLowerInvariant();
        composed.Should().Be(expected.Length is > 0 and <= SpanKey.MaxLength);
        if (composed)
        {
            destination[..written].ToString().Should().Be(expected);
        }
    }

    private static bool IsLowerAsciiByLoop(string name)
    {
        foreach (var c in name)
        {
            if (c is >= 'A' and <= 'Z' or > '')
            {
                return false;
            }
        }

        return true;
    }
}
