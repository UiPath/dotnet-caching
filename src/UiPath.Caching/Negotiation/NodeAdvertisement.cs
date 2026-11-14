using System.Globalization;
using System.Text;

namespace UiPath.Caching.Negotiation;

/// <summary>
/// What a node tells the others about itself: the value and event formats it reads, per-policy read lists, the Pub/Sub channels it has confirmed a subscription
/// to and how many pattern subscriptions it holds, a field reserved for migration capabilities, and whether the node is leaving. A node that lists no event formats reads none it can name.
/// </summary>
internal sealed record NodeAdvertisement(
    string Library,
    string Instance,
    IReadOnlyList<string> ValueReads,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Strict,
    string? Migration = null,
    bool Left = false,
    IReadOnlyList<string>? EventReads = null,
    IReadOnlyDictionary<string, int>? Subscriptions = null,
    int? PatternSubscriptions = null)
{
    public const string Json = "json";

    /// <summary>Marks a read list of a policy whose reads do not look for a header: it reads JSON only, whatever it lists.</summary>
    public const string NoSniff = "nosniff";

    public static string Token(int formatId) => formatId.ToString("X2", CultureInfo.InvariantCulture);

    public string Encode()
    {
        var text = new StringBuilder();
        text.Append("lib=").Append(Escape(Library)).Append(";inst=").Append(Escape(Instance)).Append(";vr=").AppendJoin(',', ValueReads);
        if (Strict.Count > 0)
        {
            text.Append(";strict=");
            var first = true;
            foreach (var (policy, formats) in Strict.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.Append(first ? string.Empty : ",").Append(Escape(policy)).Append(':').AppendJoin('|', formats);
                first = false;
            }
        }

        if (EventReads is not null)
        {
            text.Append(";er=").AppendJoin(',', EventReads);
        }

        if (Subscriptions is { Count: > 0 })
        {
            text.Append(";sub=");
            var first = true;
            foreach (var (channel, count) in Subscriptions.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.Append(first ? string.Empty : ",").Append(Escape(channel)).Append(':').Append(count.ToString(CultureInfo.InvariantCulture));
                first = false;
            }
        }

        if (PatternSubscriptions is > 0)
        {
            text.Append(";psub=").Append(PatternSubscriptions.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (Migration is not null)
        {
            text.Append(";mig=").Append(Escape(Migration));
        }

        if (Left)
        {
            text.Append(";left=1");
        }

        return text.ToString();
    }

    /// <summary>A field it does not know is skipped, so a later version can add fields; a text without a readable <c>vr</c> is not an advertisement.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out NodeAdvertisement? advertisement)
    {
        advertisement = null;
        string library = string.Empty, instance = string.Empty;
        string? migration = null;
        var left = false;
        IReadOnlyList<string>? reads = null, eventReads = null;
        Dictionary<string, IReadOnlyList<string>> strict = [];
        Dictionary<string, int> subscriptions = [];
        int? patterns = null;
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
                case "lib":
                    library = Unescape(value);
                    break;
                case "inst":
                    instance = Unescape(value);
                    break;
                case "vr":
                    reads = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "er":
                    eventReads = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "sub":
                    // An entry that does not parse is left out: the node then counts for fewer subscribers, which only holds writes back.
                    foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var colon = entry.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(entry[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                        {
                            subscriptions[Unescape(entry[..colon])] = count;
                        }
                    }

                    break;
                case "psub":
                    patterns = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pattern) ? pattern : null;
                    break;
                case "mig":
                    migration = Unescape(value);
                    break;
                case "left":
                    left = value == "1";
                    break;
                case "strict":
                    foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var colon = entry.LastIndexOf(':');
                        if (colon < 0)
                        {
                            return false;
                        }

                        strict[Unescape(entry[..colon])] = entry[(colon + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries);
                    }

                    break;
            }
        }

        if (reads is null)
        {
            return false;
        }

        advertisement = new NodeAdvertisement(library, instance, reads, strict, migration, left, eventReads, subscriptions.Count > 0 ? subscriptions : null, patterns);
        return true;
    }

    private static string Escape(string text) =>
        text.Replace("%", "%25", StringComparison.Ordinal)
            .Replace(";", "%3B", StringComparison.Ordinal)
            .Replace(",", "%2C", StringComparison.Ordinal)
            .Replace(":", "%3A", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("=", "%3D", StringComparison.Ordinal);

    private static string Unescape(string text) =>
        text.Replace("%3D", "=", StringComparison.Ordinal)
            .Replace("%7C", "|", StringComparison.Ordinal)
            .Replace("%3A", ":", StringComparison.Ordinal)
            .Replace("%2C", ",", StringComparison.Ordinal)
            .Replace("%3B", ";", StringComparison.Ordinal)
            .Replace("%25", "%", StringComparison.Ordinal);
}
