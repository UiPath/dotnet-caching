using UiPath.Caching.Redis;

namespace UiPath.Caching.Migration;

/// <summary>Given to the router's stack before the router exists; closed until the router's pin is attached.</summary>
internal sealed class PinFormatGate : IFormatWriteGate
{
    private volatile PinCoordinator? _pins;

    public void Attach(PinCoordinator pins) => _pins = pins;

    public string? Hold() => _pins is { } pins ? pins.Hold() : "router_not_ready";
}
