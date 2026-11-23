using System.Text;

namespace UiPath.Caching.KeyMove;

/// <summary>A static test of whether two scan patterns can match the same key.</summary>
internal static class GlobPatterns
{
    private const string Meta = "*?[\\";

    /// <summary>False only when the literal heads of the patterns differ, so no key can match both; anything else may overlap.</summary>
    public static bool MayOverlap(string first, string second)
    {
        var a = Head(first);
        var b = Head(second);
        var common = Math.Min(a.Length, b.Length);
        return string.CompareOrdinal(a, 0, b, 0, common) == 0;
    }

    /// <summary>Whether <paramref name="text"/> matches the Redis glob <paramref name="pattern"/> exactly as the server does, byte for byte over UTF-8.</summary>
    public static bool Matches(string pattern, string text)
    {
        var skipLongerMatches = false;
        return MatchBytes(Encoding.UTF8.GetBytes(pattern), Encoding.UTF8.GetBytes(text), ref skipLongerMatches, 0);
    }

    public static bool AnyMayOverlap(IReadOnlyList<string> patterns)
    {
        for (var i = 0; i < patterns.Count; i++)
        {
            for (var j = i + 1; j < patterns.Count; j++)
            {
                if (MayOverlap(patterns[i], patterns[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Head(string pattern)
    {
        var cut = pattern.AsSpan().IndexOfAny(Meta);
        return cut < 0 ? pattern : pattern[..cut];
    }

    // A port of stringmatchlen from src/util.c of Redis 7.2 (nocase off), over the UTF-8 bytes as the server sees them.
    // Where the C code reads the NUL that ends its string, At returns 0; bytes compare as signed char, as on x86 builds.
    private static bool MatchBytes(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text, ref bool skipLongerMatches, int nesting)
    {
        if (nesting > 1000)
        {
            return false;
        }

        var p = 0;
        var t = 0;
        while (p < pattern.Length && t < text.Length)
        {
            switch (pattern[p])
            {
                case (byte)'*':
                    return MatchAfterStar(pattern[p..], text[t..], ref skipLongerMatches, nesting);
                case (byte)'?':
                    t++;
                    break;
                case (byte)'[':
                    if (!MatchSet(pattern, ref p, (sbyte)text[t]))
                    {
                        return false;
                    }

                    t++;
                    break;
                default:
                    if (!MatchLiteral(pattern, ref p, text[t]))
                    {
                        return false;
                    }

                    t++;
                    break;
            }

            p++;
            if (t == text.Length)
            {
                return SkipStars(pattern, p) >= pattern.Length;
            }
        }

        return p >= pattern.Length && t == text.Length;
    }

    private static bool MatchLiteral(ReadOnlySpan<byte> pattern, ref int offset, byte value)
    {
        if (pattern[offset] == '\\' && pattern.Length - offset >= 2)
        {
            offset++;
        }

        return pattern[offset] == value;
    }

    private static int SkipStars(ReadOnlySpan<byte> pattern, int offset)
    {
        while (At(pattern, offset) == '*')
        {
            offset++;
        }

        return offset;
    }

    private static bool MatchAfterStar(ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> text, ref bool skipLongerMatches, int nesting)
    {
        var p = 0;
        while (p < pattern.Length && At(pattern, p + 1) == '*')
        {
            p++;
        }

        if (pattern.Length - p == 1)
        {
            return true;
        }

        var t = 0;
        while (t < text.Length)
        {
            if (MatchBytes(pattern[(p + 1)..], text[t..], ref skipLongerMatches, nesting + 1))
            {
                return true;
            }

            if (skipLongerMatches)
            {
                return false;
            }

            t++;
        }

        skipLongerMatches = true;
        return false;
    }

    // Reads the set that starts at pattern[p] and leaves p on its closing bracket (or just before the end when unterminated).
    private static bool MatchSet(ReadOnlySpan<byte> pattern, ref int p, int c)
    {
        p++;
        var negate = At(pattern, p) == '^';
        if (negate)
        {
            p++;
        }

        var match = false;
        while (true)
        {
            var left = pattern.Length - p;
            if (At(pattern, p) == '\\' && left >= 2)
            {
                p++;
                if ((sbyte)pattern[p] == c)
                {
                    match = true;
                }
            }
            else if (At(pattern, p) == ']')
            {
                break;
            }
            else if (left <= 0)
            {
                p--;
                break;
            }
            else if (left >= 3 && At(pattern, p + 1) == '-')
            {
                match |= InRange((sbyte)pattern[p], (sbyte)pattern[p + 2], c);
                p += 2;
            }
            else if ((sbyte)pattern[p] == c)
            {
                match = true;
            }

            p++;
        }

        return match != negate;
    }

    private static bool InRange(int start, int end, int value) =>
        value >= Math.Min(start, end) && value <= Math.Max(start, end);

    private static int At(ReadOnlySpan<byte> pattern, int index) => index < pattern.Length ? pattern[index] : 0;
}
