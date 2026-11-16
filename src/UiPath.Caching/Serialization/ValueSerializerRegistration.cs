namespace UiPath.Caching.Serialization;

/// <summary>One serializer added to a builder; a named stack receives the application's as <paramref name="Forwarded"/>, which its own of the same name replace.</summary>
internal sealed record ValueSerializerRegistration(string Name, IReadOnlyList<int> Ids, Func<IServiceProvider, ICacheValueSerializer> Factory, bool Forwarded = false)
{
    public static ValueSerializerRegistry BuildRegistry(IServiceProvider services)
    {
        var all = services.GetServices<ValueSerializerRegistration>().ToList();
        var own = all.Where(r => !r.Forwarded).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Name, ICacheValueSerializer Serializer, IReadOnlyList<int> Ids)>();
        var borrowed = new List<ICacheValueSerializer>();
        var created = new List<ICacheValueSerializer>();
        try
        {
            foreach (var registration in all.Where(r => !r.Forwarded || !own.Contains(r.Name)))
            {
                var serializer = registration.Factory(services)
                    ?? throw new InvalidOperationException($"The factory registered for value serializer '{registration.Name}' returned null.");
                (registration.Forwarded ? borrowed : created).Add(serializer);
                if (!registration.Forwarded)
                {
                    CheckReported(registration, serializer);
                }

                entries.Add((registration.Name, serializer, registration.Ids));
            }

            return new ValueSerializerRegistry(entries, borrowed);
        }
        catch
        {
            ValueSerializerRegistry.DisposeAll(created);
            throw;
        }
    }

    private static void CheckReported(ValueSerializerRegistration registration, ICacheValueSerializer serializer)
    {
        var reported = serializer.FormatIds;
        if (reported is not null && reported.Count == registration.Ids.Count && registration.Ids.ToHashSet().SetEquals(reported))
        {
            return;
        }

        var reportedText = reported is null || reported.Count == 0 ? "none" : string.Join(", ", reported.Select(FormatIdRules.Hex));
        throw new InvalidOperationException(
            $"Value serializer '{registration.Name}' was registered with format ids {string.Join(", ", registration.Ids.Select(FormatIdRules.Hex))} " +
            $"but its serializer reports {reportedText}. Declare exactly the ids it reports.");
    }
}
