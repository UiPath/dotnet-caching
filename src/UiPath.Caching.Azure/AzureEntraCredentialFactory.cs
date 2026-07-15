namespace UiPath.Caching.Azure;

internal sealed class AzureEntraCredentialFactory : IAzureEntraCredentialFactory
{
    public static AzureEntraCredentialFactory Instance { get; } = new();

    private AzureEntraCredentialFactory()
    {
    }

    public TokenCredential CreateDefaultCredential() => new DefaultAzureCredential();

    public TokenCredential CreateManagedIdentityCredential(string clientId) =>
        new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));

    public TokenCredential CreateManagedIdentityCredential(ManagedIdentityCredentialOptions options) =>
        new ManagedIdentityCredential(options);
}
