namespace UiPath.Caching.Azure;

internal interface IAzureEntraCredentialFactory
{
    TokenCredential CreateDefaultCredential();

    TokenCredential CreateManagedIdentityCredential(string clientId);

    TokenCredential CreateManagedIdentityCredential(ManagedIdentityCredentialOptions options);
}
