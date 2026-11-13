namespace UiPath.Caching.Serialization;

/// <summary>One serializer added to a builder; a named stack receives the application's as <paramref name="Forwarded"/>, which its own of the same name replace.</summary>
internal sealed record ValueSerializerRegistration(string Name, Func<IServiceProvider, ICacheValueSerializer> Factory, bool Library, bool Forwarded = false)
{
    public static ValueSerializerRegistry BuildRegistry(IServiceProvider services)
    {
        var all = services.GetServices<ValueSerializerRegistration>().ToList();
        var own = all.Where(r => !r.Forwarded).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Name, ICacheValueSerializer Serializer, bool Library)>();
        var borrowed = new List<ICacheValueSerializer>();
        var created = new List<ICacheValueSerializer>();
        try
        {
            foreach (var registration in all.Where(r => !r.Forwarded || !own.Contains(r.Name)))
            {
                var serializer = registration.Factory(services)
                    ?? throw new InvalidOperationException($"The factory registered for value serializer '{registration.Name}' returned null.");
                entries.Add((registration.Name, serializer, registration.Library));
                (registration.Forwarded ? borrowed : created).Add(serializer);
            }

            return new ValueSerializerRegistry(entries, borrowed);
        }
        catch
        {
            ValueSerializerRegistry.DisposeAll(created);
            throw;
        }
    }
}
