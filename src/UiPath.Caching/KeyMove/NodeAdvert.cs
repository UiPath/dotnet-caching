using System.Globalization;

namespace UiPath.Caching.KeyMove;

internal sealed record NodeAdvert(string Node, TimeSpan Age, IReadOnlyList<string> Tokens)
{
    public const string Km = "km";
    public const string Binding = "kmb";
    public const string Bypass = "byp";
    public const string CtlQuestion = "ctlq";
    public const int SnapshotBucket = 5;

    public bool HasKm => Has(Km);

    /// <summary>The node runs a build that keeps the marker binding: the forward generation.</summary>
    public bool HasBinding => Has(Binding);

    public bool Bypasses => Has(Bypass);

    public bool Suspects => Has(CtlQuestion);

    /// <summary>The bucketed age in seconds of the node's snapshot, or null when it advertised none.</summary>
    public int? SnapshotSeconds => Tokens.FirstOrDefault(t => t.StartsWith("snap=", StringComparison.Ordinal)) is { } t && int.TryParse(t[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : null;

    public string? Scope => Tokens.FirstOrDefault(t => t.StartsWith("ap=", StringComparison.Ordinal) || t.StartsWith("sc=", StringComparison.Ordinal))?[3..];

    /// <summary>The age rounded up to the next 5 seconds, so the token changes at most every 5 seconds and never understates the age.</summary>
    public static string Snapshot(TimeSpan age) => string.Create(CultureInfo.InvariantCulture, $"snap={(int)(Math.Ceiling(Math.Max(age.TotalSeconds, 0) / SnapshotBucket) * SnapshotBucket)}");

    public static string Flag(string group, int stage) => string.Create(CultureInfo.InvariantCulture, $"fl={group}:{stage}");

    public static string Identity(string group, AckedIdentity? acked)
    {
        if (acked is null)
        {
            return $"{group}@0";
        }

        var direction = acked.Direction == "rev" ? 'r' : 'f';
        return string.Create(CultureInfo.InvariantCulture, $"{group}@{acked.Ep}:{acked.Id}:{acked.State}{direction}");
    }

    public bool Has(string token) => Tokens.Contains(token, StringComparer.Ordinal);

    public bool Pinned(string group) => Has($"pin={group}");

    public bool Lists(string group) => Has($"kmg={group}");

    /// <summary>Returns null until the node advertises a flag reading.</summary>
    public int? FlagOf(string group) => Int(Value("fl=", group)) is { } view ? Math.Min(view, 5) : null;

    public int? PromotionUnits(string group) => Int(Value("pi=", group));

    public long? Recycled(string group) => long.TryParse(Value("rc=", group), NumberStyles.None, CultureInfo.InvariantCulture, out var ep) ? ep : null;

    /// <summary>The registered cache ids the node advertised for the group, sorted; null when it advertised none.</summary>
    public IReadOnlyList<string>? CachesOf(string group)
    {
        if (Value("ca=", group) is not { } ids)
        {
            return null;
        }

        return ids.Length == 0 ? [] : [.. ids.Split(',').Order(StringComparer.Ordinal)];
    }

    /// <summary>The identity the node runs; null for none (<c>&lt;g&gt;@0</c>, or no token).</summary>
    public AdvertIdentity? IdentityOf(string group)
    {
        var prefix = group + "@";
        var token = Tokens.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal));
        if (token is null || token.Length == prefix.Length + 1)
        {
            return null;
        }

        var parts = token[prefix.Length..].Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ep) || parts[2].Length < 3)
        {
            return null;
        }

        var direction = parts[2][^1] == 'r' ? "rev" : "fwd";
        return new AdvertIdentity(ep, parts[1], parts[2][..^1], direction);
    }

    public bool Acknowledges(string group, StateRecord state) => IdentityOf(group) is { } identity && identity.Matches(state);

    private static int? Int(string? text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private string? Value(string key, string group)
    {
        var prefix = key + group + ":";
        return Tokens.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }
}
