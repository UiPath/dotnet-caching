namespace UiPath.Caching.Azure;

internal interface IAzureEntraCredentialFactory
{
    TokenCredential CreateDefaultCredential();

    TokenCredential CreateManagedIdentityCredential();

    TokenCredential CreateManagedIdentityCredential(string clientId);

    TokenCredential CreateManagedIdentityCredential(ManagedIdentityCredentialOptions options);

    TokenCredential CreateManagedIdentityCredential(string clientId, ManagedIdentityCredentialOptions options);
}
