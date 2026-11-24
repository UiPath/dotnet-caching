namespace UiPath.Caching.KeyMove;

/// <summary>The binding of a supersession marker is data of the control record (<c>bf</c> names the field, none means <c>id</c>), so another binding changes records and not scripts.</summary>
internal static class MarkerBinding
{
    /// <summary>The record field that names the binding field; absent in the core, which binds to the identity.</summary>
    public const string BindingFieldName = "bf";

    public const string IdentityField = "id";

    public const string RetiringField = "rg";

    /// <summary>The binding value inside a marker value: what is before the first bar.</summary>
    public static string BindingOf(string marker)
    {
        var bar = marker.IndexOf('|', StringComparison.Ordinal);
        return bar < 0 ? marker : marker[..bar];
    }

    /// <summary>The marker is bound to the record's current value, or to its retiring one: it is live. An empty value binds nothing.</summary>
    public static bool IsLive(string marker, string? current, string? retiring)
    {
        var binding = BindingOf(marker);
        return (current is not null && binding == current) || (retiring is not null && binding == retiring);
    }

    /// <summary>The marker is bound to the record's current value: <c>CAP</c> answers it.</summary>
    public static bool IsCurrent(string marker, string? current) =>
        !string.IsNullOrEmpty(current) && marker.StartsWith(current + "|", StringComparison.Ordinal);
}
