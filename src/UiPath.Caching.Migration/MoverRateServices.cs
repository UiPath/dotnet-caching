using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>Registers the mover's pace: one rate for the process, so the node's cap holds across groups, and the checked options.</summary>
internal static class MoverRateServices
{
    /// <summary>Registers <see cref="AimdMoveRate"/> as the one <see cref="IMoveRate"/> singleton over the process's <see cref="IScanUnits"/>.</summary>
    public static IServiceCollection AddMoverRate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<KeyMoveMoverOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<KeyMoveMoverOptions>, KeyMoveMoverOptionsValidator>());
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton(sp => AimdMoveRate.CreateDefault(sp.GetRequiredService<IScanUnits>(), sp.GetRequiredService<IOptionsMonitor<KeyMoveMoverOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IMoveRate>(sp => sp.GetRequiredService<AimdMoveRate>());
        return services;
    }
}
