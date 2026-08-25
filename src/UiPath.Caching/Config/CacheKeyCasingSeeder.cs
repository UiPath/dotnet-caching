namespace UiPath.Caching.Config;

/// <summary>
/// Validates <see cref="CacheOptions.KeyCasing"/> and seeds <see cref="CacheKey.DefaultCasing"/> from it.
/// Runs as options validation rather than a PostConfigure callback so it observes the final value: callbacks
/// run in registration order, so a consumer post-configuring <c>KeyCasing</c> after <c>AddCaching</c> would
/// change the resolved options while leaving the process-global default at whatever was seeded first —
/// <c>new CacheKey("AbC")</c> lowercasing while <c>IOptions&lt;CacheOptions&gt;.Value</c> reported otherwise.
/// </summary>
internal sealed class CacheKeyCasingSeeder : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        if (name is not null && name != Options.DefaultName)
        {
            return ValidateOptionsResult.Skip;
        }

        if (options.KeyCasing is not (CacheKeyCasing.Insensitive or CacheKeyCasing.Sensitive))
        {
            return ValidateOptionsResult.Fail(
                $"CacheOptions.KeyCasing has the unsupported value {(int)options.KeyCasing}. Use {nameof(CacheKeyCasing.Insensitive)} or {nameof(CacheKeyCasing.Sensitive)}.");
        }

        CacheKey.DefaultCasing = options.KeyCasing;
        return ValidateOptionsResult.Success;
    }
}
