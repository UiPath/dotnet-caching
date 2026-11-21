using System.Globalization;
using Microsoft.Extensions.Logging;

namespace UiPath.Caching.Migration;

/// <summary>The one startup line that says which settings are in force and which one wins.</summary>
internal static class RouterBanner
{
    public static string Describe(RouterBuilder builder, string processId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Caching router settings: caches={builder.Registrations.Count}; identity process id={processId} ({(builder.NodeIdentity is null ? "generated" : "UseNodeIdentity")}); " +
            $"group predicate=UseLibraryWhen; bypass={(builder.BypassFlag is null ? "none (EnterBypassAsync only)" : "UseBypassWhen")}; pin={(builder.PinPredicate is null ? "none" : "UsePinWhen")}; write format={(builder.WriteFormatPredicate is null ? "stack default" : "UseWriteFormatWhen")}; " +
            $"source timeout={builder.SourceTimeout ?? TimeSpan.FromSeconds(30)} ({(builder.SourceTimeout is null ? "default" : "UseSourceTimeout")}); " +
            $"settle bound={(builder.SettleBound is { } bound ? $"{bound} (UseLibrarySettleBound)" : "from the connection")}; " +
            $"precedence, first wins: {string.Join(" > ", RoutePrecedence.Order)}.");

    public static void Log(ILogger logger, RouterBuilder builder, string processId) => logger.LogInformation("{Banner}", Describe(builder, processId));
}
