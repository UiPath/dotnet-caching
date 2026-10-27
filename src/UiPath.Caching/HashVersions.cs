using System.Runtime.CompilerServices;

namespace UiPath.Caching;

/// <summary>Versions live beside the entries rather than in them, since a custom entry factory builds the entries.</summary>
internal static class HashVersions
{
    private static readonly ConditionalWeakTable<object, HashVersion> Stamps = [];

    internal static void Stamp<T>(object entry, long number, DateTimeOffset? localDeadline, OwnWrite[]? own = null) =>
        Stamps.AddOrUpdate(entry, new HashVersion<T>(number, own ?? [], localDeadline));

    internal static bool TryGet(object entry, [MaybeNullWhen(false)] out HashVersion version) =>
        Stamps.TryGetValue(entry, out version);

    internal static void Carry(object from, object to)
    {
        if (Stamps.TryGetValue(from, out var version))
        {
            Stamps.AddOrUpdate(to, version);
        }
    }

    internal static long NumberOf(object entry) =>
        Stamps.TryGetValue(entry, out var version) ? version.Number : 0;
}
