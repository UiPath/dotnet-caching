namespace UiPath.Caching.Migration;

internal sealed partial class RouterCore
{
    /// <summary>Set before the caches are built and null when the router has no pin setting, in which case no call does any pin work.</summary>
    public PinCoordinator? Pins { get; set; }

    /// <summary>Set with the pin when the router has a write format setting; null leaves every library write on the stack default.</summary>
    public WriteFormatSelector? WriteFormats { get; set; }

    public GroupPin? PinFor(string group) => Pins?.GroupFor(group);

    /// <summary>Asked again at the point a call would take the source as its fallback: the pin must still be unapplied and its state read recently enough.</summary>
    public bool MaySourceFallback(GroupPin? pin) => pin is null || (!pin.Applied && pin.StateKnown(Clock, Pins!.StateValidity));

    /// <summary>
    /// The route a captured source write may still take: asked again right before it is sent, as a factory or a lock can hold it past the pin and the state it was admitted under.
    /// A pin that applied since sends it to the library, and a state too old to tell leaves it unanswered.
    /// </summary>
    public Route Revalidate(GroupPin? pin, Route route)
    {
        if (pin is null || route.Path != RoutePath.Source)
        {
            return route;
        }

        if (pin.Applied)
        {
            return RoutePrecedence.Pinned(route.Tier);
        }

        return pin.StateKnown(Clock, Pins!.StateValidity) ? route : RoutePrecedence.Unknown;
    }

    /// <summary>
    /// Admits a call of a group that can be pinned. A group the pin applies to goes to the library and never the source; one that is not known to be
    /// unswitched is answered unavailable; any other keeps its usual path, and is counted until it ends so the pin can tell when the old path has drained.
    /// </summary>
    public Admission Admit(GroupPin pin, string group, out Route route)
    {
        var admission = Admit();
        var flag = ReadFlag();
        var inBypass = admission.Mode == BypassIndex || !LibraryStarted;
        var tierOpen = Volatile.Read(ref _tierClosed) == 0;
        if (!pin.TryBeginSource())
        {
            route = RoutePrecedence.Pinned(tierOpen && !inBypass && flag == FlagState.Off);
            return admission;
        }

        admission = admission.WithPin(pin);
        if (!pin.StateKnown(Clock, Pins!.StateValidity))
        {
            route = RoutePrecedence.Unknown;
            return admission;
        }

        route = RoutePrecedence.Resolve(inBypass, !LibraryStarted, flag, tierOpen, useLibrary, group);
        return admission;
    }
}
