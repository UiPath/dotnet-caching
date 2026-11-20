namespace UiPath.Caching.Migration;

/// <summary>Why a router is in <see cref="RouterMode.BypassLibrary"/>; <see cref="Default"/> when it is not.</summary>
public enum RouterModeReason
{
    Default,
    Flag,
    Requested,
    LibraryUnavailable,
}
