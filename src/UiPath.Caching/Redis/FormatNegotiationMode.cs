namespace UiPath.Caching.Redis;

public enum FormatNegotiationMode
{
    /// <summary>Write a non-JSON format only while every node on the transport is known to read it.</summary>
    Auto,

    /// <summary>Write exactly what each policy configures, with no node list.</summary>
    Force,
}
