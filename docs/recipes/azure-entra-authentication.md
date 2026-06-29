# Azure Managed Redis with Microsoft Entra ID

**What:** Authenticate to Azure Cache for Redis / Azure Managed Redis using a Microsoft Entra ID
identity (managed identity, workload identity, or service principal) instead of an access key — via
the `Caching.Azure` package and `AddAzureEntraAuthentication()` on the caching builder (the `b` in
`services.AddCaching(..., b => ...)`).

**When to use:**
- Your Redis is Azure Managed Redis / Azure Cache for Redis with Entra (Azure AD) authentication enabled.
- You want passwordless auth via a managed identity, with tokens refreshed automatically.

## Install

```bash
dotnet add package UiPath.Caching.Azure
```

The core `Caching.Runtime` package does **not** depend on the Azure SDK; the Azure dependency lives
only in `Caching.Azure`.

## Code

Point the connection string at the Azure Redis endpoint (no key) and add Entra auth:

```csharp
using UiPath.Caching.Config;

var section = builder.Configuration.GetSection("Caching");
builder.Services.AddCaching(section, b =>
{
    b.AddRedisConnection()            // Connections:Redis:ConnectionString = "myamr.region.redis.azure.net:10000"
     .AddAzureEntraAuthentication()   // binds Caching:AzureEntra by default; empty section uses DefaultAzureCredential
     .AddRedis()
     .AddInMemoryRedis();
}, o => section.Bind(o));
```

Variants:

```csharp
using Azure.Identity;

// User-assigned managed identity
b.AddAzureEntraAuthentication(o => o.ManagedIdentityClientId = "<client-id>");

// Managed identity with Azure.Identity options, such as a sovereign cloud authority.
// Include ManagedIdentityClientId for user-assigned MI, or omit it for system-assigned MI.
b.AddAzureEntraAuthentication(o =>
{
    o.ManagedIdentityClientId = "<client-id>";
    o.ManagedIdentityOptions = new ManagedIdentityCredentialOptions
    {
        AuthorityHost = AzureAuthorityHosts.AzureGovernment,
    };
});

// Any Azure.Core TokenCredential (service principal, workload identity, chained, ...)
b.AddAzureEntraAuthentication(o => o.Credential = new ManagedIdentityCredential());

// Bind from a custom config section name instead of the default "AzureEntra"
b.AddAzureEntraAuthentication("CustomAzureEntra");
```

Default config shape:

```json
{
  "Caching": {
    "AzureEntra": {
      "ManagedIdentityClientId": "<client-id>",
      "ManagedIdentityOptions": {
        "AuthorityHost": "https://login.microsoftonline.us/"
      }
    }
  }
}
```

`ManagedIdentityOptions` is `Azure.Identity.ManagedIdentityCredentialOptions`; see Microsoft Learn
for the full option surface:
[ManagedIdentityCredentialOptions](https://learn.microsoft.com/dotnet/api/azure.identity.managedidentitycredentialoptions).

By default the connection is established lazily on first cache use. Set
`RedisConnectionOptions.WarmUpOnStart = true` to kick it off at host startup instead (best-effort —
it never fails startup), so the first cache call doesn't pay the connection cost.

Note that with `PlannedMaintenanceEnabled = true` (the default) the planned-maintenance hosted
service opens its own multiplexer — and acquires an Entra token — at startup regardless of
`WarmUpOnStart`. Set `PlannedMaintenanceEnabled = false` if you need startup to stay fully lazy.

## Notes

- **Token refresh is automatic.** `Caching.Azure` delegates to `Microsoft.Azure.StackExchangeRedis`,
  which proactively refreshes the Entra token (~5 minutes before expiry) and re-authenticates live
  connections, keeping long-running connections alive for days.
- **Applied once per connect.** The configurator runs each time the library creates a multiplexer —
  initial connect and after a `ForceReconnect` — so a reconnect picks up a freshly configured
  `ConfigurationOptions`.
- **Credential precedence.** `AzureEntraOptions.Credential` wins when supplied. If it is `null`,
  `ManagedIdentityOptions` creates a `ManagedIdentityCredential`; with `ManagedIdentityClientId` this
  is a user-assigned managed identity, and without it this is the system-assigned managed identity.
- **CLI reaches the endpoint, Redis authorizes the identity.** If
  `az redisenterprise test-connection --auth entra` prints `Successfully connected` and then fails
  `PING` with `invalid username-password pair`, network/TLS is working but the Entra object id is not
  accepted as a Redis user for the database. Add or verify a database access-policy assignment for the
  object id printed by the CLI:

  ```powershell
  az redisenterprise database access-policy-assignment list `
    --resource-group <resource-group> `
    --cluster-name <cache-name> `
    --database-name default `
    --output table

  az redisenterprise database access-policy-assignment create `
    --resource-group <resource-group> `
    --cluster-name <cache-name> `
    --database-name default `
    --access-policy-assignment-name <alphanumeric-assignment-name> `
    --access-policy-name default `
    --object-id <object-id>
  ```

  The assignment name is an Azure resource name validated by the CLI; use only letters and digits,
  without hyphens.
  For managed identities, appsettings uses the user-assigned identity **client id**, while Redis access
  policy assignment uses the Entra **object id/principal id**.
- **TLS is forced on by default** (`AzureEntraOptions.RequireSsl`, default `true`), as Azure Managed
  Redis requires it. Set it to `false` only for non-TLS test endpoints.
- **RESP3 by default** (`AzureEntraOptions.RequireResp3`, default `true`) — sets `Protocol = Resp3` so
  pub/sub rides the main connection that the Azure extension re-authenticates. Under RESP2,
  StackExchange.Redis uses a separate subscription connection that cannot be re-authenticated in place,
  so it drops when the Entra token expires and broadcast messages can be missed during reconnect. Set
  it to `false` to keep the StackExchange.Redis/connection-string default.
- **Composes with custom factories.** Auth is applied to the `ConfigurationOptions` before the
  `IConnectionMultiplexerFactory` runs, so it works alongside the OpenTelemetry factory recipe.

## Custom authentication methods

Entra is just one implementation of the core `IRedisConnectionConfigurator` extension point. To authenticate a
different way (AWS IAM, a custom token source, another cloud), implement the interface and register
it — no `Caching.Azure` reference, no core change:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using UiPath.Caching.Redis;

public sealed class AwsIamRedisConfigurator : IRedisConnectionConfigurator
{
    public async ValueTask ConfigureAsync(ConfigurationOptions options, CancellationToken ct = default)
    {
        options.Ssl = true;
        options.Password = await GenerateAwsIamAuthTokenAsync(ct);
    }
}

// registration (inside your AddCaching builder callback)
b.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IRedisConnectionConfigurator, AwsIamRedisConfigurator>());
```

## See also

- [how-to/extending.md](../how-to/extending.md#redis-connection-authentication)
- [recipes/opentelemetry-multiplexer-factory.md](opentelemetry-multiplexer-factory.md)
