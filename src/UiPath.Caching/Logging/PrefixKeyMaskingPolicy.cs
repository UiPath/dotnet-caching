namespace UiPath.Caching.Logging;

/// <summary>A key is a secret when it starts with one of the configured prefixes, or when none is configured; an identifier stays readable either way.</summary>
public sealed class PrefixKeyMaskingPolicy : IKeyMaskingPolicy
{
    private readonly string[] _prefixes;

    public PrefixKeyMaskingPolicy(params string[] maskedKeyPrefixes)
    {
        ArgumentNullException.ThrowIfNull(maskedKeyPrefixes);
        _prefixes = [.. maskedKeyPrefixes];
    }

    public bool ShouldMask(in MaskingContext context)
    {
        if (string.IsNullOrEmpty(context.Key) || KeyMasker.IsIdentifier(context.Key))
        {
            return false;
        }

        if (_prefixes.Length == 0)
        {
            return true;
        }

        var key = context.Key;
        return _prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
