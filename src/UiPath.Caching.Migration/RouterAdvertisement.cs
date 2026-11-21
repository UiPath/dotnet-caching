using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace UiPath.Caching.Migration;

/// <summary>A router's entry in the node hash, <c>kind=router;inst=..;build=..;mig=t1+t2[;left=1]</c>, with no <c>vr</c> field, so the format negotiation does not count it.</summary>
internal sealed record RouterAdvertisement(string Instance, string Build, IReadOnlyList<string> Tokens, bool Left = false)
{
    /// <summary>The router reports <see cref="RouterMode.BypassLibrary"/>.</summary>
    public const string Bypass = "byp";

    /// <summary>Appended to the node id, so a process's router entry and its library advertisement are two fields.</summary>
    public const string FieldSuffix = "#router";

    private const string Kind = "router";

    public bool Has(string token) => Tokens.Contains(token, StringComparer.Ordinal);

    public string Encode()
    {
        var text = new StringBuilder();
        text.Append("kind=").Append(Kind)
            .Append(";inst=").Append(Escape(Instance))
            .Append(";build=").Append(Escape(Build))
            .Append(";mig=").Append(Escape(string.Join('+', Tokens)));
        if (Left)
        {
            text.Append(";left=1");
        }

        return text.ToString();
    }

    /// <summary>Unknown fields and empty tokens are skipped; a text that is not a router entry does not parse.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out RouterAdvertisement? advertisement)
    {
        advertisement = null;
        string? kind = null, instance = null, build = null;
        string[] tokens = [];
        var left = false;
        foreach (var field in text.Split(';'))
        {
            var separator = field.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var value = field[(separator + 1)..];
            switch (field[..separator])
            {
                case "kind":
                    kind = value;
                    break;
                case "inst":
                    instance = Unescape(value);
                    break;
                case "build":
                    build = Unescape(value);
                    break;
                case "mig":
                    tokens = Unescape(value).Split('+', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "left":
                    left = value == "1";
                    break;
            }
        }

        if (kind != Kind || instance is null)
        {
            return false;
        }

        advertisement = new RouterAdvertisement(instance, build ?? string.Empty, tokens, left);
        return true;
    }

    private static string Escape(string text) =>
        text.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);

    private static string Unescape(string text) =>
        text.Replace("%7C", "|", StringComparison.Ordinal)
            .Replace("%3D", "=", StringComparison.Ordinal)
            .Replace("%3B", ";", StringComparison.Ordinal)
            .Replace("%25", "%", StringComparison.Ordinal);
}
