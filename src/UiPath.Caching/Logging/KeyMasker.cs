using System.Globalization;

namespace UiPath.Caching;

/// <summary>One cache's view of the policy; rendering lives here so a custom policy cannot emit the raw key.</summary>
internal sealed class KeyMasker
{
    private const int Revealed = 3;
    private const string MaskText = "****";

    public static readonly KeyMasker Off = new(NullKeyMaskingPolicy.Instance, string.Empty);

    /// <summary>For keys known to be the consumer's, whatever the application registered.</summary>
    public static readonly KeyMasker Always = new(AlwaysMaskKeyMaskingPolicy.Instance, string.Empty);

    private readonly IKeyMaskingPolicy _policy;
    private readonly string _cacheName;

    public KeyMasker(IKeyMaskingPolicy policy, string cacheName)
    {
        _policy = policy ?? NullKeyMaskingPolicy.Instance;
        _cacheName = cacheName ?? string.Empty;
    }

    public static KeyMasker For(IKeyMaskingPolicy? policy, string cacheName) =>
        policy is null or NullKeyMaskingPolicy ? Off : new KeyMasker(policy, cacheName);

    /// <summary>Splices the caller's key out of <paramref name="composed"/> wherever it sits; a composed key it is not part of is masked whole.</summary>
    public string Render(string key, string? composed, Type? valueType)
    {
        var shown = composed ?? key;
        if (!ShouldMask(key, valueType))
        {
            return shown;
        }

        if (key.Length == 0 || shown.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return Mask(shown);
        }

        // Every occurrence: a strategy is free to repeat the key, in a cluster hash tag and again in the body.
        return shown.Replace(key, Mask(key), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A policy is application code running inside log formatting: a throwing one masks rather than escapes.</summary>
    private bool ShouldMask(string key, Type? valueType)
    {
        if (ReferenceEquals(_policy, NullKeyMaskingPolicy.Instance))
        {
            return false;
        }

        try
        {
            return _policy.ShouldMask(new MaskingContext(key, valueType, _cacheName));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>First three characters then <c>****</c>: enough to correlate two lines, not to replay a key.</summary>
    public static string Mask(string value) =>
        value.Length > Revealed ? string.Concat(value.AsSpan(0, Revealed), MaskText) : MaskText;

    /// <summary>A number or a GUID names a row, not a person; the built-in policy leaves those readable.</summary>
    public static bool IsIdentifier(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || Guid.TryParse(value, out _);
}
