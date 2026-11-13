namespace UiPath.Caching;

public static class CacheValueFormats
{
    public const int MessagePack = 0x02;
    public const int MessagePackLz4 = 0x03;
    public const int FirstConsumer = 0x10;
    public const int LastConsumer = 0x1E;
    public const int FirstExtended = 0x100;
    public const int LastExtended = 0x1FF;
    public const string JsonName = "json";
}
