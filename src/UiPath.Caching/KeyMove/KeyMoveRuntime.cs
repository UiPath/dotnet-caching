namespace UiPath.Caching.KeyMove;

/// <summary>The parts of a started key move node that operator commands and the router use.</summary>
internal sealed record KeyMoveRuntime(
    StateStore Store,
    StageCommands Commands,
    GroupDataOps Data,
    ClearCoordinator Clear,
    GroupRemoval Removal,
    StateRecovery Recovery,
    StageDriver? Driver);
