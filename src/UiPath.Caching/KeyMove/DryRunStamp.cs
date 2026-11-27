using System.Globalization;

namespace UiPath.Caching.KeyMove;

/// <summary>The <c>dry</c> field: <c>&lt;unix ms&gt;|&lt;estimated N bytes&gt;|&lt;summary&gt;</c>.</summary>
internal sealed record DryRunStamp(DateTimeOffset At, long EstimatedBytes, string Summary)
{
    /// <summary>The identity of the report: the time it was stored, so a newer dry run is another report.</summary>
    public string ReportId => At.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    public static DryRunStamp? TryParse(string text)
    {
        var parts = text.Split('|', 3);
        if (parts.Length < 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
        {
            return null;
        }

        return new DryRunStamp(DateTimeOffset.FromUnixTimeMilliseconds(ms), bytes, parts.Length == 3 ? parts[2] : string.Empty);
    }

    public string Encode() => string.Create(CultureInfo.InvariantCulture, $"{At.ToUnixTimeMilliseconds()}|{EstimatedBytes}|{Summary}");
}
