namespace UiPath.Caching.KeyMove;

internal static class KeyMoveStates
{
    public const string Off = "S0";
    public const string Preparing = "S1";
    public const string Switching = "S2";
    public const string Copying = "S3";
    public const string Copied = "S4";
    public const string Done = "S5";

    private static readonly (string Stored, string Name)[] Stages =
    [
        (Off, "off"),
        (Preparing, "preparing"),
        (Switching, "switching"),
        (Copying, "copying"),
        (Copied, "copied"),
        (Done, "done"),
    ];

    public static string NameOf(string stored) => Stages.FirstOrDefault(s => s.Stored == stored).Name ?? stored;

    public static string NameOf(string stored, string direction) => direction == "rev"
        ? stored switch
        {
            Off => "off",
            Preparing => "returning",
            Copying => "copying-back",
            Copied => "copied-back",
            _ => NameOf(stored) + " (reverse)",
        }
        : NameOf(stored);

    public static string? StoredOf(string name) => Stages.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).Stored;

    public static int? ParseFlag(string? value)
    {
        value = value?.Trim();
        for (var stage = 0; stage < Stages.Length; stage++)
        {
            if ((Stages[stage].Stored is not (Preparing or Switching) && string.Equals(value, Stages[stage].Name, StringComparison.OrdinalIgnoreCase)) ||
                (value is { Length: 1 } && value[0] == '0' + stage))
            {
                return stage;
            }
        }

        return null;
    }
}
