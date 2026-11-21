using System.Text;

namespace UiPath.Caching.PhaseB;

/// <summary>
/// The claim record's grammar, the same as the scripts' shared header byte for byte: ASCII classes as Lua's %a, %w, %x and %d, whole-string anchors,
/// lengths in UTF-8 bytes as Lua's #. Admission, the fallback and the operator commands all read through it.
/// </summary>
internal static class ClaimGrammar
{
    public const string Pending = "pend";
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Idle = "idle";
    public const string Pointer = "cur:fm";

    /// <summary>The empty pointer.</summary>
    public const string NoPointer = "-";

    public static bool IsLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    public static bool IsDigit(char c) => c is >= '0' and <= '9';

    public static bool IsAlnum(char c) => IsLetter(c) || IsDigit(c);

    public static bool IsHex(char c) => IsDigit(c) || c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    /// <summary>Lua's #: the length in bytes.</summary>
    public static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    public static bool ValidGroup(string g) =>
        Bytes(g) is >= 1 and <= 200 && All(g, static c => IsAlnum(c) || c is '.' or '_' or ':' or '>' or '@' or '+' or '-');

    /// <summary>The kind and group of a field name: a move kind, <c>prv</c>, or <c>cur</c> with <c>fm</c>; null outside the grammar.</summary>
    public static (string Kind, string Group)? ValidField(string f)
    {
        if (Bytes(f) > 204)
        {
            return null;
        }

        var letters = 0;
        while (letters < f.Length && IsLetter(f[letters]))
        {
            letters++;
        }

        if (letters == 0 || letters >= f.Length - 1 || f[letters] != ':')
        {
            return null;
        }

        var kind = f[..letters];
        var group = f[(letters + 1)..];
        if (!ValidGroup(group))
        {
            return null;
        }

        return kind switch
        {
            "rt" or "fm" or "im" or "imd" or "prv" => (kind, group),
            "cur" when group == "fm" => (kind, group),
            _ => null,
        };
    }

    public static bool IsMove(string? kind) => kind is not null and not "cur" and not "prv";

    /// <summary><c>imd</c> counts as <c>im</c>.</summary>
    public static string Normalize(string kind) => kind == "imd" ? "im" : kind;

    public static bool ValidId(string i) => Bytes(i) is >= 1 and <= 8 && All(i, IsAlnum);

    public static bool ValidDescriptor(string d)
    {
        if (Bytes(d) is 0 or > 40)
        {
            return false;
        }

        if (d.StartsWith("fmt:", StringComparison.Ordinal))
        {
            var rest = d[4..];
            var arrow = rest.IndexOf('>', StringComparison.Ordinal);
            if (arrow > 0 && All(rest[..arrow], IsAlnum) && All(rest[(arrow + 1)..], IsAlnum) && rest.Length > arrow + 1)
            {
                return ValidId(rest[..arrow]) && ValidId(rest[(arrow + 1)..]);
            }
        }

        return d is "inst" or "rt";
    }

    public static bool ValidToken(string t) => Bytes(t) == 32 && All(t, IsHex);

    public static bool ValidEpoch(string e) => Bytes(e) is >= 1 and <= 18 && All(e, IsDigit);

    public static bool ValidCert(string c)
    {
        if (c.Length == 0)
        {
            return true;
        }

        var colon = c.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon <= 18 && All(c[..colon], IsDigit) && c[(colon + 1)..] is "S0" or "S5";
    }

    /// <summary>A move value: null when absent, a malformed marker, or its four parts.</summary>
    public static ClaimValue? Parse(string? v)
    {
        if (v is null)
        {
            return null;
        }

        var parts = v.Split('|');
        if (parts.Length != 4)
        {
            return ClaimValue.Malformed;
        }

        var (s, t, d, c) = (parts[0], parts[1], parts[2], parts[3]);
        var shaped = s.Length > 0 && All(s, IsLetter) && t.Length > 0 && All(t, IsHex) && d.Length > 0;
        if (!shaped || s is not (Pending or Open or Closed or Idle) || !ValidToken(t) || !ValidDescriptor(d) || !ValidCert(c))
        {
            return ClaimValue.Malformed;
        }

        return new ClaimValue(s, t, d, c);
    }

    /// <summary>A pointer: null when absent, a malformed marker, the empty pointer for <c>-</c>, or its four parts.</summary>
    public static ClaimPointer? ParsePointer(string? v)
    {
        if (v is null)
        {
            return null;
        }

        if (v == NoPointer)
        {
            return ClaimPointer.Empty;
        }

        var parts = v.Split('|');
        if (parts.Length != 4)
        {
            return ClaimPointer.Malformed;
        }

        var (g, t, a, b) = (parts[0], parts[1], parts[2], parts[3]);
        var shaped = g.Length > 0 && t.Length > 0 && All(t, IsHex) && a.Length > 0 && All(a, IsAlnum) && b.Length > 0 && All(b, IsAlnum);
        if (!shaped || !ValidGroup(g) || !ValidToken(t) || !ValidId(a) || !ValidId(b))
        {
            return ClaimPointer.Malformed;
        }

        return new ClaimPointer(g, t, a, b);
    }

    private static bool All(string s, Func<char, bool> test)
    {
        foreach (var c in s)
        {
            if (!test(c))
            {
                return false;
            }
        }

        return true;
    }
}
