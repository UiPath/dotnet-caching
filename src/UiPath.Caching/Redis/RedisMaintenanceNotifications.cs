namespace UiPath.Caching.Redis;

/// <summary>Whether to ask the server for advance notice of maintenance, where the server offers it.</summary>
public enum RedisMaintenanceNotifications
{
    /// <summary>Never ask.</summary>
    Disabled = 0,

    /// <summary>Ask, and connect normally if the server does not offer them.</summary>
    Auto = 1,

    /// <summary>Ask, and refuse the connection if the server will not deliver them.</summary>
    /// <remarks>Except inside a multi-group (geo-redundant) connection, where the client warns and connects.</remarks>
    Required = 2,
}
