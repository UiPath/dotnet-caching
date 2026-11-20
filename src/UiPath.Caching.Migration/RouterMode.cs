namespace UiPath.Caching.Migration;

/// <summary>Which client family serves calls: the library with the shared local tier, or only the source (older) client.</summary>
public enum RouterMode
{
    Library,
    BypassLibrary,
}
