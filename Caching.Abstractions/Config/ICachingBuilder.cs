using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace UiPath.Platform.Caching.Config;

public interface ICachingBuilder
{
    IServiceCollection Services { get; }

    IConfiguration Configuration { get; }

    bool Enabled { get; set; }

    void RegisterOnCompleteCallback(object key, Action<ICachingBuilder> callback);
}
