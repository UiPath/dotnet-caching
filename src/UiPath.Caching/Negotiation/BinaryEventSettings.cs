namespace UiPath.Caching.Negotiation;

/// <summary>What the binary event formatter registered: this node decodes binary events, and whether it is set to write them.</summary>
internal sealed class BinaryEventSettings(bool write)
{
    public const int FormatId = 0x02;

    public static string Token => NodeAdvertisement.Token(FormatId);

    public bool Write { get; set; } = write;
}
