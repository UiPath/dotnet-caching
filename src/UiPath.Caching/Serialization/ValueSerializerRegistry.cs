namespace UiPath.Caching.Serialization;

/// <summary>Disposes the serializers it holds except the <c>borrowed</c> ones, which another registry owns.</summary>
internal sealed class ValueSerializerRegistry : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, ICacheValueSerializer> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ICacheValueSerializer> _byFormat = [];
    private readonly Dictionary<ICacheValueSerializer, int[]> _idsOf = new(ReferenceEqualityComparer.Instance);
    private readonly List<(string Name, ICacheValueSerializer Serializer, bool Library)> _entries = [];
    private readonly HashSet<ICacheValueSerializer> _borrowed = new(ReferenceEqualityComparer.Instance);

    public ValueSerializerRegistry(
        IEnumerable<(string Name, ICacheValueSerializer Serializer, bool Library)> entries,
        IEnumerable<ICacheValueSerializer>? borrowed = null)
    {
        foreach (var (name, serializer, library) in entries)
        {
            Add(name, serializer, library);
        }

        _borrowed.UnionWith(borrowed ?? []);
    }

    public static ValueSerializerRegistry Empty { get; } = new([]);

    public bool IsEmpty => _byName.Count == 0;

    public IReadOnlyList<(string Name, ICacheValueSerializer Serializer, bool Library)> Entries => _entries;

    public static string UnregisteredMessage(string policy, string field, string name) =>
        $"{policy} names the value serializer '{name}' in Serializer.{field}, but none with that name is registered. " +
        "Register it with AddValueSerializer, or use \"json\".";

    /// <summary>Null for <see cref="CacheValueFormats.JsonName"/>: the default JSON path.</summary>
    public ICacheValueSerializer? Resolve(string? name) =>
        TryResolve(name, out var serializer)
            ? serializer
            : throw new ArgumentException($"No value serializer named '{name}' is registered.", nameof(name));

    public bool TryResolve(string? name, out ICacheValueSerializer? serializer)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            serializer = null;
            return false;
        }

        if (string.Equals(name, CacheValueFormats.JsonName, StringComparison.OrdinalIgnoreCase))
        {
            serializer = null;
            return true;
        }

        return _byName.TryGetValue(name, out serializer);
    }

    /// <summary>The ids as they were when the serializer was added and validated, whatever <see cref="ICacheValueSerializer.FormatIds"/> reports since.</summary>
    public ReadOnlyMemory<int> FormatIdsOf(ICacheValueSerializer serializer) =>
        _idsOf.TryGetValue(serializer, out var ids)
            ? ids
            : throw new InvalidOperationException($"{serializer.GetType().Name} is not registered here.");

    /// <summary>The serializer the policy writes with, resolving a <c>Write</c> name the policy factory did not; null writes JSON.</summary>
    public ICacheValueSerializer? WriteSerializerFor(CachePolicy? policy, string? policyName = null)
    {
        if (policy is null)
        {
            return null;
        }

        if (policy.WriteSerializer is { } resolved)
        {
            return resolved;
        }

        if (policy.Serializer?.Write is not { } name)
        {
            return null;
        }

        if (TryResolve(name, out var serializer))
        {
            return serializer;
        }

        var owner = policyName is null ? "A cache policy" : $"Cache policy '{policyName}'";
        throw new OptionsValidationException(
            nameof(CacheOptions),
            typeof(CacheOptions),
            [UnregisteredMessage(owner, nameof(SerializerPolicy.Write), name)]);
    }

    /// <summary>Checks the <c>Write</c> name of the factory's default and named policies, which a custom factory never had validated.</summary>
    public void ValidateWriteNames(ICachePolicyFactory factory)
    {
        WriteSerializerFor(factory.Default, "default");
        foreach (var key in factory.Keys)
        {
            WriteSerializerFor(factory.Resolve(key), key);
        }
    }

    public void Dispose() => DisposeAll(Owned());

    public async ValueTask DisposeAsync()
    {
        foreach (var serializer in Owned())
        {
            switch (serializer)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    public bool TryGetByFormat(int formatId, [NotNullWhen(true)] out ICacheValueSerializer? serializer) =>
        _byFormat.TryGetValue(formatId, out serializer);

    internal static void DisposeAll(IEnumerable<ICacheValueSerializer> serializers)
    {
        foreach (var serializer in serializers)
        {
            switch (serializer)
            {
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                case IAsyncDisposable asyncDisposable:
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
            }
        }
    }

    private static bool IsLibraryId(int id) => id is >= 0x01 and <= 0x0F and not 0x09 and not 0x0A and not 0x0D;

    private static bool IsConsumerId(int id) =>
        id is >= CacheValueFormats.FirstConsumer and <= CacheValueFormats.LastConsumer
            or >= CacheValueFormats.FirstExtended and <= CacheValueFormats.LastExtended;

    [DoesNotReturn]
    private static void Invalid(string message) =>
        throw new OptionsValidationException(nameof(CacheOptions), typeof(CacheOptions), [message]);

    private IEnumerable<ICacheValueSerializer> Owned() =>
        _entries.Select(e => e.Serializer).Where(serializer => !_borrowed.Contains(serializer));

    private void Add(string name, ICacheValueSerializer serializer, bool library)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(serializer);
        CheckName(name);

        var declared = serializer.FormatIds;
        if (declared is null)
        {
            Invalid($"Value serializer '{name}' returns null from FormatIds; it must declare at least one format id.");
        }

        int[] ids = [.. declared];
        if (ids.Length == 0)
        {
            Invalid($"Value serializer '{name}' declares no format ids.");
        }

        var extended = ids[0] >= CacheValueFormats.FirstExtended;
        var claimed = new HashSet<int>();
        foreach (var id in ids)
        {
            CheckId(name, id, library, extended, claimed);
        }

        _byName.Add(name, serializer);
        _entries.Add((name, serializer, library));
        _idsOf.Add(serializer, ids);
        foreach (var id in claimed)
        {
            _byFormat.Add(id, serializer);
        }
    }

    private void CheckName(string name)
    {
        if (string.Equals(name, CacheValueFormats.JsonName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' is reserved for the built-in JSON format.", nameof(name));
        }

        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"A value serializer named '{name}' is already registered.", nameof(name));
        }
    }

    private void CheckId(string name, int id, bool library, bool extended, HashSet<int> claimed)
    {
        if (!(library ? IsLibraryId(id) : IsConsumerId(id)))
        {
            Invalid(
                $"Value serializer '{name}' declares format id 0x{id:X2}, which is not " +
                (library ? "a library id (0x01-0x0F except 0x09, 0x0A, 0x0D)." : "a consumer id (0x10-0x1E or 0x100-0x1FF)."));
        }

        if ((id >= CacheValueFormats.FirstExtended) != extended)
        {
            Invalid($"Value serializer '{name}' mixes one-byte and extended format ids; all of its ids need the same header length.");
        }

        if (!claimed.Add(id) || _byFormat.ContainsKey(id))
        {
            Invalid($"Format id 0x{id:X2} is declared more than once (value serializer '{name}').");
        }
    }
}
