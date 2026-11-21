namespace UiPath.Caching.Migration;

internal sealed partial class RouterCore
{
    /// <summary>Set before the caches are built and null when the router has no pin setting, in which case no call does any pin work.</summary>
    public PinCoordinator? Pins { get; set; }

    public GroupPin? PinFor(string group) => Pins?.GroupFor(group);

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
