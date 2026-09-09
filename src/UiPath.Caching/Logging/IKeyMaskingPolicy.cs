namespace UiPath.Caching.Logging;

/// <summary>What the library is about to name in a log line, so a policy can judge whether it is a secret.</summary>
/// <param name="ValueType">What the cache holds, when the call site knows it: <c>ICache&lt;SessionToken&gt;</c> is a secret, <c>ICache&lt;int&gt;</c> is not.</param>
public readonly record struct MaskingContext(string Key, Type? ValueType, string CacheName);

/// <summary>Decides whether a cache key is a secret; registered once and consulted by every component that logs a key.</summary>
public interface IKeyMaskingPolicy
{
    bool ShouldMask(in MaskingContext context);
}

/// <summary>Masks nothing; what a container resolves until masking is configured.</summary>
public sealed class NullKeyMaskingPolicy : IKeyMaskingPolicy
{
    public static readonly NullKeyMaskingPolicy Instance = new();

    private NullKeyMaskingPolicy()
    {
    }

    public bool ShouldMask(in MaskingContext context) => false;
}

/// <summary>Masks every key it is asked about. The distributed adapter uses it: those keys are the consumer's.</summary>
public sealed class AlwaysMaskKeyMaskingPolicy : IKeyMaskingPolicy
{
    public static readonly AlwaysMaskKeyMaskingPolicy Instance = new();

    private AlwaysMaskKeyMaskingPolicy()
    {
    }

    public bool ShouldMask(in MaskingContext context) => true;
}
