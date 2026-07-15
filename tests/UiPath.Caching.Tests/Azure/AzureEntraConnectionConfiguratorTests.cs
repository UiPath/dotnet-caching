using Azure.Core;
using Azure.Identity;
using StackExchange.Redis;
using UiPath.Caching.Azure;

namespace UiPath.Caching.Tests.Azure;

public class AzureEntraConnectionConfiguratorTests
{
    private sealed class CapturingConfigurator : AzureEntraConnectionConfigurator
    {
        public CapturingConfigurator(IOptions<AzureEntraOptions> options)
            : base(options)
        {
        }

        public CapturingConfigurator(IOptions<AzureEntraOptions> options, IAzureEntraCredentialFactory credentialFactory)
            : base(options, credentialFactory)
        {
        }

        public bool Applied { get; private set; }
        public bool SslAtApplyTime { get; private set; }
        public TokenCredential? CapturedCredential { get; private set; }

        protected override Task ApplyAzureAuthenticationAsync(ConfigurationOptions configuration, TokenCredential credential)
        {
            Applied = true;
            SslAtApplyTime = configuration.Ssl;
            CapturedCredential = credential;
            return Task.CompletedTask;
        }
    }

    private enum CredentialFactoryCall
    {
        None,
        Default,
        ManagedIdentityClientId,
        ManagedIdentityOptions,
    }

    private sealed class CapturingCredentialFactory(TokenCredential credential) : IAzureEntraCredentialFactory
    {
        public CredentialFactoryCall Call { get; private set; }
        public int CallCount { get; private set; }
        public string? ClientId { get; private set; }
        public ManagedIdentityCredentialOptions? Options { get; private set; }

        public TokenCredential CreateDefaultCredential()
        {
            CallCount++;
            Call = CredentialFactoryCall.Default;
            return credential;
        }

        public TokenCredential CreateManagedIdentityCredential(string clientId)
        {
            CallCount++;
            Call = CredentialFactoryCall.ManagedIdentityClientId;
            ClientId = clientId;
            return credential;
        }

        public TokenCredential CreateManagedIdentityCredential(ManagedIdentityCredentialOptions options)
        {
            CallCount++;
            Call = CredentialFactoryCall.ManagedIdentityOptions;
            Options = options;
            return credential;
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => default;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => default;
    }

    private static CapturingConfigurator Create(AzureEntraOptions options, IAzureEntraCredentialFactory? credentialFactory = null)
    {
        var configuredOptions = Options.Create(options);
        return credentialFactory is null
            ? new CapturingConfigurator(configuredOptions)
            : new CapturingConfigurator(configuredOptions, credentialFactory);
    }

    [Fact]
    public async Task ConfigureAsync_EnablesSsl_ByDefault()
    {
        var sut = Create(new AzureEntraOptions());
        var config = new ConfigurationOptions();

        await sut.ConfigureAsync(config, TestContext.Current.CancellationToken);

        config.Ssl.Should().BeTrue();
        sut.Applied.Should().BeTrue();
        sut.SslAtApplyTime.Should().BeTrue();
    }

    [Fact]
    public async Task ConfigureAsync_DoesNotForceSsl_WhenRequireSslIsFalse()
    {
        var sut = Create(new AzureEntraOptions { RequireSsl = false });
        var config = new ConfigurationOptions();

        await sut.ConfigureAsync(config, TestContext.Current.CancellationToken);

        config.Ssl.Should().BeFalse();
    }

    [Fact]
    public async Task ConfigureAsync_EnablesResp3_ByDefault()
    {
        var sut = Create(new AzureEntraOptions());
        var config = new ConfigurationOptions();

        await sut.ConfigureAsync(config, TestContext.Current.CancellationToken);

        config.Protocol.Should().Be(RedisProtocol.Resp3);
    }

    [Fact]
    public async Task ConfigureAsync_DoesNotSetProtocol_WhenRequireResp3IsFalse()
    {
        var sut = Create(new AzureEntraOptions { RequireResp3 = false });
        var config = new ConfigurationOptions();

        await sut.ConfigureAsync(config, TestContext.Current.CancellationToken);

        config.Protocol.Should().BeNull();
    }

    [Fact]
    public async Task ConfigureAsync_DoesNotOverrideExplicitProtocol()
    {
        var sut = Create(new AzureEntraOptions());
        var config = new ConfigurationOptions { Protocol = RedisProtocol.Resp2 };

        await sut.ConfigureAsync(config, TestContext.Current.CancellationToken);

        config.Protocol.Should().Be(RedisProtocol.Resp2);
    }

    [Fact]
    public async Task ConfigureAsync_UsesProvidedCredential()
    {
        var credential = new FakeCredential();
        var factory = new CapturingCredentialFactory(new FakeCredential());
        var sut = Create(new AzureEntraOptions { Credential = credential }, factory);

        await sut.ConfigureAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        sut.CapturedCredential.Should().BeSameAs(credential);
        factory.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ConfigureAsync_UsesManagedIdentityClientId()
    {
        var credential = new FakeCredential();
        var factory = new CapturingCredentialFactory(credential);
        var sut = Create(new AzureEntraOptions { ManagedIdentityClientId = "managed-identity-client-id" }, factory);

        await sut.ConfigureAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        sut.CapturedCredential.Should().BeSameAs(credential);
        factory.Call.Should().Be(CredentialFactoryCall.ManagedIdentityClientId);
        factory.ClientId.Should().Be("managed-identity-client-id");
    }

    [Fact]
    public async Task ConfigureAsync_UsesManagedIdentityOptions()
    {
        var options = new ManagedIdentityCredentialOptions
        {
            AuthorityHost = AzureAuthorityHosts.AzureGovernment,
        };
        var credential = new FakeCredential();
        var factory = new CapturingCredentialFactory(credential);
        var sut = Create(new AzureEntraOptions { ManagedIdentityOptions = options }, factory);

        await sut.ConfigureAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        sut.CapturedCredential.Should().BeSameAs(credential);
        factory.Call.Should().Be(CredentialFactoryCall.ManagedIdentityOptions);
        factory.Options.Should().BeSameAs(options);
    }

    [Fact]
    public async Task ConfigureAsync_ManagedIdentityOptions_TakePrecedenceOverClientId()
    {
        var options = new ManagedIdentityCredentialOptions
        {
            AuthorityHost = AzureAuthorityHosts.AzureGovernment,
        };
        var credential = new FakeCredential();
        var factory = new CapturingCredentialFactory(credential);
        var sut = Create(new AzureEntraOptions
        {
            ManagedIdentityClientId = "managed-identity-client-id",
            ManagedIdentityOptions = options,
        }, factory);

        await sut.ConfigureAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        sut.CapturedCredential.Should().BeSameAs(credential);
        factory.Call.Should().Be(CredentialFactoryCall.ManagedIdentityOptions);
        factory.Options.Should().BeSameAs(options);
        factory.ClientId.Should().BeNull();
    }

    [Fact]
    public async Task ConfigureAsync_FallsBackToDefaultAzureCredential()
    {
        var credential = new FakeCredential();
        var factory = new CapturingCredentialFactory(credential);
        var sut = Create(new AzureEntraOptions(), factory);

        await sut.ConfigureAsync(new ConfigurationOptions(), TestContext.Current.CancellationToken);

        sut.CapturedCredential.Should().BeSameAs(credential);
        factory.Call.Should().Be(CredentialFactoryCall.Default);
    }

    [Fact]
    public void AzureEntraCredentialFactory_CreatesExpectedCredentialTypes()
    {
        var factory = AzureEntraCredentialFactory.Instance;

        factory.CreateDefaultCredential().Should().BeOfType<DefaultAzureCredential>();
        factory.CreateManagedIdentityCredential("managed-identity-client-id").Should().BeOfType<ManagedIdentityCredential>();
        factory.CreateManagedIdentityCredential(new ManagedIdentityCredentialOptions()).Should().BeOfType<ManagedIdentityCredential>();
    }
}
