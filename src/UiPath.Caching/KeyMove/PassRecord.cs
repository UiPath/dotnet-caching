using System.Globalization;

namespace UiPath.Caching.KeyMove;

/// <summary>The record of a unit's clean finished pass: the identity and move it ran under, when it ended, how many keys failed, and the server it scanned.</summary>
internal static class PassRecord
{
    public static string Text(string identity, long moveId, long completedAt, int failed, string source) =>
        string.Create(CultureInfo.InvariantCulture, $"{identity}|{moveId}|{completedAt}|{failed}|{source}");

    /// <summary>The server a recorded pass scanned (endpoint and run id); empty for a record that names none.</summary>
    public static string SourceOf(string? record) => record?.Split('|') is [_, _, _, _, var source] ? source : string.Empty;

    /// <summary>True when the record names exactly this identity and move id and no key of the pass failed; no clock is read.</summary>
    public static bool IsCleanFor(string? record, string identity, long moveId) =>
        record?.Split('|') is [var id, var move, _, var failed, _] && id == identity && move == moveId.ToString(CultureInfo.InvariantCulture) && failed == "0";
}
