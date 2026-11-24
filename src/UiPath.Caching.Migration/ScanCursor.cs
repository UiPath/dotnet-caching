using System.Globalization;

namespace UiPath.Caching.Migration;

/// <summary>Where a unit's pass stands, bound to what it is valid for: the pattern and SCAN cursor, the state identity, the server that issued the cursor, the pattern list, and how many keys failed so far.</summary>
internal sealed record ScanCursor(int Pattern, string Cursor, string Identity, string Source, string Patterns, int Failed)
{
    /// <summary>True at the first call of a pass: pattern 0 and cursor 0.</summary>
    public bool IsStart => Pattern == 0 && Cursor == "0";

    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Pattern}|{Cursor}|{Identity}|{Source}|{Patterns}|{Failed}");

    /// <summary>Null for no cursor, or for text that is not one, which starts the pass again.</summary>
    public static ScanCursor? Parse(string? text) =>
        text?.Split('|') is [var pattern, var cursor, var identity, var source, var patterns, var failed]
        && int.TryParse(pattern, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
        && ulong.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out _)
        && int.TryParse(failed, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? new ScanCursor(index, cursor, identity, source, patterns, count)
            : null;

    /// <summary>The start of a pass: pattern 0, cursor 0, bound to nothing until the first scan names its server.</summary>
    public static ScanCursor Start(string identity, string source, string patterns) => new(0, "0", identity, source, patterns, 0);
}
