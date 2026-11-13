namespace UiPath.Caching.Negotiation;

/// <summary>What the binary event formatter registered: this node decodes binary events, and whether it is set to write them.</summary>
internal sealed class BinaryEventSettings(bool write)
{
    public static string Token => NodeAdvertisement.Token(CacheValueFormats.MessagePack);

    public bool Write { get; set; } = write;
}
