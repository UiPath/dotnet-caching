namespace UiPath.Caching.KeyMove;

internal static class KeyMoveStates
{
    public const string NotStarted = "S0";
    public const string Ready = "S1";
    public const string Barrier = "S2";
    public const string Moving = "S3";
    public const string Drained = "S4";
    public const string Closed = "S5";

    private static readonly (string Stored, string Name)[] Stages =
    [
        (NotStarted, "not-started"),
        (Ready, "ready"),
        (Barrier, "barrier"),
        (Moving, "moving"),
        (Drained, "drained"),
        (Closed, "closed"),
    ];

    public static string NameOf(string stored) => Stages.FirstOrDefault(s => s.Stored == stored).Name ?? "unknown";

    public static string? StoredOf(string name) => Stages.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Stored;

    public static int? ParseFlag(string? value)
    {
        for (var stage = 0; stage < Stages.Length; stage++)
        {
            if (string.Equals(value, Stages[stage].Name, StringComparison.OrdinalIgnoreCase) ||
                (value is { Length: 1 } && value[0] == '0' + stage))
            {
                return stage;
            }
        }

        return null;
    }
}
