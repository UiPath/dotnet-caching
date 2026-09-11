namespace UiPath.Caching.Logging;

/// <summary>Decides whether a cache key is a secret; registered once and consulted by every component that logs a key.</summary>
public interface IKeyMaskingPolicy
{
    bool ShouldMask(in MaskingContext context);
}
