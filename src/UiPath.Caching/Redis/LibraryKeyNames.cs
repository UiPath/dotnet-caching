namespace UiPath.Caching.Redis;

/// <summary>Names the library composes for its own keys inside the string and hash keyspaces.</summary>
internal static class LibraryKeyNames
{
    public static string NodeList(char separator) =>
        KeyEscaping.EscapeBraces(string.Join(separator, "Caching", "Nodes"));

    public static string MaintainerLock(char separator) =>
        KeyEscaping.EscapeBraces(string.Join(separator, "RedisStreamHealthMaintainer", "Lock2"));

    public static string QuarantineMarker(char separator) =>
        string.Join(separator, "caching", "meta", "stream") + "$";
}
