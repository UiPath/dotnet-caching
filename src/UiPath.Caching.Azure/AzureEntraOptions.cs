namespace UiPath.Caching.Azure;

/// <summary>Options for Microsoft Entra ID authentication against Azure (Managed) Redis.</summary>
[ExcludeFromCodeCoverage]
public sealed class AzureEntraOptions
{
    /// <summary>The credential used to acquire Entra tokens. Takes precedence over <see cref="ManagedIdentityClientId"/>.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>Client id of a user-assigned managed identity. Used when <see cref="Credential"/> is null.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Options for the managed identity credential. Used when <see cref="Credential"/> is null.</summary>
    public ManagedIdentityCredentialOptions? ManagedIdentityOptions { get; set; }

    /// <summary>Force TLS on the connection. Defaults to <c>true</c>.</summary>
    public bool RequireSsl { get; set; } = true;

    /// <summary>Negotiate RESP3 so Entra pub/sub stays on the re-authenticated connection. Defaults to <c>true</c>.</summary>
    public bool RequireResp3 { get; set; } = true;
}
