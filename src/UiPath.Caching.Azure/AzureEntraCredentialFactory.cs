namespace UiPath.Caching.Azure;

internal sealed class AzureEntraCredentialFactory : IAzureEntraCredentialFactory
{
    public static AzureEntraCredentialFactory Instance { get; } = new();

    private AzureEntraCredentialFactory()
    {
    }

    public TokenCredential CreateDefaultCredential() => new DefaultAzureCredential();

    public TokenCredential CreateManagedIdentityCredential() => new ManagedIdentityCredential();

    public TokenCredential CreateManagedIdentityCredential(string clientId) => new ManagedIdentityCredential(clientId);

    public TokenCredential CreateManagedIdentityCredential(ManagedIdentityCredentialOptions options) => new ManagedIdentityCredential(options);

    public TokenCredential CreateManagedIdentityCredential(string clientId, ManagedIdentityCredentialOptions options) => new ManagedIdentityCredential(clientId, options);
}
