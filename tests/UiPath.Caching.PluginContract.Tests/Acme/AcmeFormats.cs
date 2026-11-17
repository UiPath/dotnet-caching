namespace UiPath.Caching.PluginContract.Tests.Acme;

/// <summary>The ids of the fake third-party plugin: extended value ids and a consumer-range event id, none of them in the allocation table.</summary>
public static class AcmeFormats
{
    public const string Name = "acme";
    public const int Value = 0x100;
    public const int ValueGzip = 0x101;
    public const int Event = 0x11;

    public static IReadOnlyList<int> ValueIds { get; } = [Value, ValueGzip];
}
