# AWS and Google Cloud IAM authentication

**What:** Connect to Amazon ElastiCache, Amazon MemoryDB, Memorystore for Redis Cluster or Memorystore for Valkey with a short-lived IAM token instead of a static password. You write one `IRedisConnectionConfigurator` in your app.

**When to use:**
- The server has IAM authentication turned on.
- You want no stored password and your app already uses the AWS SDK or the Google auth library.

There is no `UiPath.Caching.*` package for this. The library adds no AWS or Google dependency. Your app adds the AWS or Google packages itself. The Azure case has a package, see [azure-entra-authentication.md](azure-entra-authentication.md).

## The seam

The library calls every registered `IRedisConnectionConfigurator` before it builds a multiplexer. The interface has one method:

```csharp
ValueTask ConfigureAsync(ConfigurationOptions configuration, CancellationToken cancellationToken = default);
```

The call happens on the first connect and again whenever the library rebuilds the multiplexer, for example after `ForceReconnect`. Your configurator sets `User`, `Password` and `Ssl` on the options. The Azure package does the same, then calls `ConfigureForAzureWithTokenCredentialAsync`. That Azure helper also refreshes the token and re-authenticates live connections. The sample code below does not. See [Token refresh](#token-refresh).

## AWS: ElastiCache and MemoryDB

How the token works, from the AWS docs:

- The token is a SigV4 presigned request. You send it as the password in `AUTH` or `HELLO`.
- It is valid for 15 minutes.
- An IAM connection is closed after 12 hours. Sending `AUTH` or `HELLO` with a new token extends it by 12 hours.
- Re-authentication is not supported inside `MULTI`/`EXEC` or Lua. MemoryDB lists `MULTI`/`EXEC` only.
- ElastiCache needs TLS, Valkey 7.2+ or Redis OSS 7.0+. MemoryDB needs Valkey or Redis OSS 7.0+.
- The ElastiCache user name and user id must be identical. Write the cache name in lowercase.
- The IAM identity needs `elasticache:Connect` or `memorydb:connect` on the cache and on the user.

AWS publishes no .NET sample. The signing steps below follow the Java `IAMAuthTokenRequest` class in the AWS docs. The service name is `elasticache` or `memorydb`. The request is `GET` on `http://<cache-name>/` with `Action=connect` and `User=<user>`. Serverless ElastiCache adds `ResourceType=ServerlessCache`. The token is the presigned URL without `http://`.

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using StackExchange.Redis;
using UiPath.Caching.Redis;

public sealed class AwsIamRedisConfigurator(
    string cacheName, string user, string region, string service = "elasticache", bool serverless = false)
    : IRedisConnectionConfigurator
{
    public async ValueTask ConfigureAsync(ConfigurationOptions options, CancellationToken ct = default)
    {
        var credentials = await DefaultAWSCredentialsIdentityResolver.GetCredentials().GetCredentialsAsync();
        options.Ssl = true;
        options.User = user;
        options.Password = Presign(credentials, DateTime.UtcNow);
    }

    private string Presign(ImmutableCredentials c, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var scope = $"{stamp[..8]}/{region}/{service}/aws4_request";
        var q = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Action"] = "connect", ["User"] = user, ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{c.AccessKey}/{scope}", ["X-Amz-Date"] = stamp,
            ["X-Amz-Expires"] = "900", ["X-Amz-SignedHeaders"] = "host",
        };
        if (serverless) q["ResourceType"] = "ServerlessCache";
        if (!string.IsNullOrEmpty(c.Token)) q["X-Amz-Security-Token"] = c.Token;
        var query = string.Join('&', q.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var request = $"GET\n/\n{query}\nhost:{cacheName}\n\nhost\n{Hex(SHA256.HashData(Array.Empty<byte>()))}";
        var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(request)))}";
        var key = Encoding.UTF8.GetBytes("AWS4" + c.SecretKey);
        foreach (var part in new[] { stamp[..8], region, service, "aws4_request" }) key = Hmac(key, part);
        return $"{cacheName}/?{query}&X-Amz-Signature={Hex(Hmac(key, toSign))}";
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
```

The app references `AWSSDK.Core` 4.x for `DefaultAWSCredentialsIdentityResolver`. For MemoryDB pass `service: "memorydb"`. Use the cluster name as `cacheName`.

Register it inside the `AddCaching` builder callback. The constructor takes arguments, so use a factory:

```csharp
b.Services.AddSingleton<IRedisConnectionConfigurator>(
    _ => new AwsIamRedisConfigurator("my-cache", "my-iam-user", "us-east-1"));
```

The Azure recipe uses `TryAddEnumerable`. That method does not accept a factory registration, so this one uses `AddSingleton`.

## Google Cloud: Memorystore for Redis Cluster and for Valkey

How the token works, from the Google docs:

- The password is an IAM access token. You send it with `AUTH`.
- The token lives one hour by default. You can extend it to 12 hours.
- A connection that is already authenticated keeps working after the token expires. A new connection needs a valid token.
- The user name is optional. If you set one, use `default` or the principal email address (documented for the Cluster product).
- Google recommends turning on in-transit encryption (TLS) with IAM auth. The server CA comes from the instance.
- Grant `roles/redis.dbConnectionUser` (Cluster) or `roles/memorystore.dbConnectionUser` (Valkey).
- Google auth libraries cache tokens. A cached token near expiry can fail the next handshake. Fetch a token for each new connection.
- IAM auth throttles the number of new connections per second. The page gives no number.

```csharp
using Google.Apis.Auth.OAuth2;
using StackExchange.Redis;
using UiPath.Caching.Redis;

public sealed class GoogleIamRedisConfigurator : IRedisConnectionConfigurator
{
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly Lazy<Task<GoogleCredential>> _credential = new(async () =>
        (await GoogleCredential.GetApplicationDefaultAsync()).CreateScoped(Scope));

    public async ValueTask ConfigureAsync(ConfigurationOptions options, CancellationToken ct = default)
    {
        var credential = await _credential.Value;
        var token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
        options.Ssl = true;
        options.Password = token;
    }
}
```

The app references `Google.Apis.Auth`. You must still make StackExchange.Redis trust the instance CA. Set `options.CertificateValidation` with the CA file from the instance. The sample leaves that out.

Register it:

```csharp
b.Services.AddSingleton<IRedisConnectionConfigurator, GoogleIamRedisConfigurator>();
```

## Token refresh

StackExchange.Redis keeps the password it was given at connect. It reuses that password when it reconnects a dropped link. Neither sample re-authenticates a live connection. This is our reading and it is not tested.

- **AWS.** After 12 hours AWS closes the connection. A reconnect inside the same multiplexer presents the old token, which is 15 minutes old at most, and AWS rejects an expired token.
- **Google.** A live connection survives token expiry. A reconnect after expiry presents the old token and a new handshake fails.

The library rebuilds the multiplexer on hang detection, stale endpoint detection and planned maintenance. Whether it also rebuilds after an authentication failure is not verified. To stay safe, rebuild on a timer. A rebuild runs the configurator again and gets a fresh token. It also resets every connection, so pick a period that suits you.

```csharp
public sealed class IamTokenRenewal(IRedisConnector connector) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(stoppingToken)) connector.ForceReconnect();
    }
}
```

A 10 minute period keeps the AWS token inside its 15 minute life and the Google token inside its hour. Use a longer period if the rebuild cost matters. The 12 hour AWS limit is the ceiling for AWS.

## Not verified

- The AWS signing code compiles against `AWSSDK.Core` 4.1, and its token matches botocore's `SigV4QueryAuth` byte for byte for fixed credentials and time. It was not run against a live cache.
- The Google sample compiles against `Google.Apis.Auth` 1.78. It was not run against a live instance.
- Whether the library rebuilds the multiplexer after an authentication failure.
- Whether StackExchange.Redis re-sends `AUTH` with a new token on its own. The sample assumes it does not.
- IAM authentication for Memorystore for Redis (non-cluster). The Google page was not reachable.
- The Google OAuth scope. Google names the `cloud-platform` scope for Compute Engine service accounts. It does not name a scope for a client library.
- The `ForceReconnect` timer was not run against either cloud.

## See also

- [azure-entra-authentication.md](azure-entra-authentication.md)
- [how-to/extending.md](../how-to/extending.md)
- [reference/server-support.md](../reference/server-support.md)

## Sources

- https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/auth-iam.html
- https://docs.aws.amazon.com/memorydb/latest/devguide/auth-iam.html
- https://docs.cloud.google.com/memorystore/docs/cluster/about-iam-auth
- https://docs.cloud.google.com/memorystore/docs/cluster/manage-iam-auth
- https://docs.cloud.google.com/memorystore/docs/valkey/about-iam-auth
