namespace UiPath.Caching.Logging;

/// <summary>Masks nothing; what a container resolves until masking is configured.</summary>
public sealed class NullKeyMaskingPolicy : IKeyMaskingPolicy
{
    public static readonly NullKeyMaskingPolicy Instance = new();

    private NullKeyMaskingPolicy()
    {
    }

    public bool ShouldMask(in MaskingContext context) => false;
}
