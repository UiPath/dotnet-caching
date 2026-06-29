using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UiPath.Caching.Azure;
using UiPath.Caching.Config;
using UiPath.Caching.Redis;

namespace UiPath.Caching.Tests.Azure;

public class AzureCachingBuilderExtensionsTests
{
    [Fact]
    public void AddAzureEntraAuthentication_RegistersSingleConfigurator_AndIsIdempotent()
    {
        var services = new ServiceCollection();
        var builder = Substitute.For<ICachingBuilder>();
        builder.Services.Returns(services);

        builder.AddAzureEntraAuthentication();
        builder.AddAzureEntraAuthentication();

        var descriptors = services.Where(d => d.ServiceType == typeof(IRedisConnectionConfigurator)).ToList();
        descriptors.Should().HaveCount(1);
        descriptors[0].ImplementationType.Should().Be<AzureEntraConnectionConfigurator>();
    }

    [Fact]
    public void AddAzureEntraAuthentication_WithSectionName_BindsOptionsFromConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CustomAzureEntra:ManagedIdentityClientId"] = "mi-from-config",
                ["CustomAzureEntra:RequireSsl"] = "false",
            })
            .Build();
        var builder = Substitute.For<ICachingBuilder>();
        builder.Services.Returns(services);
        builder.Configuration.Returns(configuration);

        builder.AddAzureEntraAuthentication("CustomAzureEntra");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AzureEntraOptions>>().Value;
        options.ManagedIdentityClientId.Should().Be("mi-from-config");
        options.RequireSsl.Should().BeFalse();
    }

    [Fact]
    public void AddAzureEntraAuthentication_BindsDefaultAzureEntraSectionFromConfiguration()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureEntra:ManagedIdentityClientId"] = "mi-from-default-section",
                ["AzureEntra:RequireResp3"] = "false",
            })
            .Build();
        var builder = Substitute.For<ICachingBuilder>();
        builder.Services.Returns(services);
        builder.Configuration.Returns(configuration);

        builder.AddAzureEntraAuthentication();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AzureEntraOptions>>().Value;
        options.ManagedIdentityClientId.Should().Be("mi-from-default-section");
        options.RequireResp3.Should().BeFalse();
    }
}
