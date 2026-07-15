namespace UiPath.Caching.Azure;

/// <summary>Options for Microsoft Entra ID authentication against Azure (Managed) Redis.</summary>
[ExcludeFromCodeCoverage]
public sealed class AzureEntraOptions
{
    /// <summary>The credential used to acquire Entra tokens. Takes precedence over all managed-identity settings below.</summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Client id of a user-assigned managed identity. Used when both <see cref="Credential"/> and
    /// <see cref="ManagedIdentityOptions"/> are null. To use a user-assigned identity together with additional
    /// credential options, leave this null and set <see cref="ManagedIdentityOptions"/> to
    /// <c>new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(clientId))</c> — the
    /// identity now lives inside the options object rather than being passed alongside it.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// Options for the managed identity credential. The target identity is carried by the
    /// <see cref="ManagedIdentityCredentialOptions"/> itself (set via its constructor; defaults to the
    /// system-assigned identity). Used when <see cref="Credential"/> is null and, when set, takes precedence over
    /// <see cref="ManagedIdentityClientId"/> (which is then ignored).
    /// </summary>
    public ManagedIdentityCredentialOptions? ManagedIdentityOptions { get; set; }

    /// <summary>Force TLS on the connection. Defaults to <c>true</c>.</summary>
    public bool RequireSsl { get; set; } = true;

    /// <summary>Negotiate RESP3 so Entra pub/sub stays on the re-authenticated connection. Defaults to <c>true</c>.</summary>
    public bool RequireResp3 { get; set; } = true;
}
