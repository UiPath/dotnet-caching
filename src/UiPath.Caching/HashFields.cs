namespace UiPath.Caching;

/// <summary>The Redis tier's field rules, which the multilayer RemoveFieldsAsync also applies before it touches the local tier.</summary>
internal static class HashFields
{
    internal static void ValidateForWrite<T>(IDictionary<string, T?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ValidateFieldsForWrite(values.Keys);
    }

    internal static void ValidateFieldsForWrite(ICollection<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var key in fields)
        {
            ValidateFieldForWrite(key);
        }
    }

    internal static void ValidateFieldForWrite(string field)
    {
        ValidateFieldShape(field);
        if (KnownFieldNames.IsReserved(field))
        {
            throw new ArgumentException($"Field name '{field}' follows the reserved '_word_' pattern and is reserved for system metadata (e.g. {KnownFieldNames.MetadataKey}).", nameof(field));
        }
    }

    internal static void ValidateFieldsForRead(ICollection<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var key in fields)
        {
            ValidateFieldForRead(key);
        }
    }

    internal static void ValidateFieldForRead(string field)
    {
        ValidateFieldShape(field);
        if (KnownFieldNames.IsSystemField(field))
        {
            throw new ArgumentException($"Field name '{field}' is reserved for system metadata and cannot be read directly.", nameof(field));
        }
    }

    internal static void ValidateFieldShape(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }
    }
}
