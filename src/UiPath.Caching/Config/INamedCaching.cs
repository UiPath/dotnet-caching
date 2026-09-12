namespace UiPath.Caching.Config;

/// <summary>A caching stack registered by <see cref="NamedCachingCollectionExtensions.AddNamedCaching"/>.</summary>
public interface INamedCaching
{
    string Name { get; }

    /// <summary>The application's collection, where the keyed services are registered.</summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Re-exposes one of the stack's services as a keyed singleton under <see cref="Name"/>, absent when the stack does
    /// not register it. The stack disposes what it owns, and the application's container disposes the exposed instance
    /// too, which <see cref="IDisposable"/> already requires to be safe.
    /// </summary>
    INamedCaching Expose<TService>()
        where TService : class;

    /// <inheritdoc cref="Expose{TService}()"/>
    /// <param name="resolve">Builds the value from the stack's own service provider.</param>
    INamedCaching Expose<TService>(Func<IServiceProvider, TService> resolve)
        where TService : class;
}
