using System.Globalization;

namespace UiPath.Caching.KeyMove;

/// <summary>The record of a unit's clean finished pass: the identity and move it ran under, when it ended, how many keys failed, the server it scanned, and the scan plan.</summary>
internal static class PassRecord
{
    // The completion stamp carries the plan while the failure count and scan source keep their field positions.
    public static string Text(string identity, long moveId, long completedAt, int failed, string source, string scanPlan) =>
        string.Create(CultureInfo.InvariantCulture, $"{identity}|{moveId}|{completedAt}:{scanPlan}|{failed}|{source}");

    /// <summary>The server a recorded pass scanned (endpoint and run id); empty for a record that names none.</summary>
    public static string SourceOf(string? record) => record?.Split('|') is [_, _, _, _, var source] ? source : string.Empty;

    /// <summary>True when the record names exactly this identity, move id and scan plan and no key of the pass failed; no clock is read.</summary>
    public static bool IsCleanFor(string? record, string identity, long moveId, string scanPlan) =>
        record?.Split('|') is [var id, var move, var completion, var failed, _]
        && id == identity
        && move == moveId.ToString(CultureInfo.InvariantCulture)
        && failed == "0"
        && completion.Split(':') is [_, var plan]
        && plan == scanPlan;
}
