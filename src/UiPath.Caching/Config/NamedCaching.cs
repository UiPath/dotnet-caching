namespace UiPath.Caching.Config;

internal sealed class NamedCaching(string name, IServiceCollection services) : INamedCaching
{
    public string Name => name;

    public IServiceCollection Services => services;

    // Optional like the unkeyed side, so what the chain does not register reads as absent instead of failing every lookup.
    public INamedCaching Expose<TService>()
        where TService : class =>
        Expose(stack => stack.GetService<TService>()!);

    public INamedCaching Expose<TService>(Func<IServiceProvider, TService> resolve)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(resolve);
        services.AddKeyedSingleton(name, (sp, key) => resolve(NamedCachingContainer.Of(sp, (string)key!).Provider));
        return this;
    }
}
