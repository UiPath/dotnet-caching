namespace UiPath.Caching.Azure;

/// <summary>Configures a Redis connection to authenticate with Microsoft Entra ID.</summary>
public class AzureEntraConnectionConfigurator : IRedisConnectionConfigurator
{
    private readonly AzureEntraOptions _options;
    private readonly Lazy<TokenCredential> _credential;

    public AzureEntraConnectionConfigurator(IOptions<AzureEntraOptions> options)
        : this(options, AzureEntraCredentialFactory.Instance)
    {
    }

    internal AzureEntraConnectionConfigurator(IOptions<AzureEntraOptions> options, IAzureEntraCredentialFactory credentialFactory)
    {
        _options = options.Value;
        _credential = new Lazy<TokenCredential>(() => CreateCredential(_options, credentialFactory));
    }

    public async ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_options.RequireSsl)
        {
            configuration.Ssl = true;
        }

        if (_options.RequireResp3)
        {
            configuration.Protocol ??= RedisProtocol.Resp3;
        }

        await ApplyAzureAuthenticationAsync(configuration, _credential.Value).ConfigureAwait(false);
    }

    private static TokenCredential CreateCredential(AzureEntraOptions options, IAzureEntraCredentialFactory credentialFactory)
    {
        if (options.Credential is not null)
        {
            return options.Credential;
        }

        if (options.ManagedIdentityOptions is not null)
        {
            return string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? credentialFactory.CreateManagedIdentityCredential(options.ManagedIdentityOptions)
                : credentialFactory.CreateManagedIdentityCredential(options.ManagedIdentityClientId, options.ManagedIdentityOptions);
        }

        return string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? credentialFactory.CreateDefaultCredential()
            : credentialFactory.CreateManagedIdentityCredential(options.ManagedIdentityClientId);
    }

    /// <summary>Applies Entra authentication via Microsoft.Azure.StackExchangeRedis. Virtual for testability.</summary>
    [ExcludeFromCodeCoverage(Justification = "Acquires a live Entra token via Microsoft.Azure.StackExchangeRedis — needs Azure to exercise.")]
    protected virtual Task ApplyAzureAuthenticationAsync(ConfigurationOptions configuration, TokenCredential credential) =>
        configuration.ConfigureForAzureWithTokenCredentialAsync(credential);
}
