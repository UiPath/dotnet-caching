namespace UiPath.Caching.Logging;

/// <summary>Masks every key it is asked about. The distributed adapter uses it: those keys are the consumer's.</summary>
public sealed class AlwaysMaskKeyMaskingPolicy : IKeyMaskingPolicy
{
    public static readonly AlwaysMaskKeyMaskingPolicy Instance = new();

    private AlwaysMaskKeyMaskingPolicy()
    {
    }

    public bool ShouldMask(in MaskingContext context) => true;
}
