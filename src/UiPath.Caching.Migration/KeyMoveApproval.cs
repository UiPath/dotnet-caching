namespace UiPath.Caching.Migration;

/// <summary>How a stored approval ended.</summary>
public enum KeyMoveApproval
{
    /// <summary>The approval is stored for the group's current state.</summary>
    Stored,

    /// <summary>The group has no key move state; initialize it first.</summary>
    NoState,

    /// <summary>The state moved while the approval was being stored; read the status and approve again if it still applies.</summary>
    StateMoved,

    /// <summary>The kind does not apply to the group's current stage.</summary>
    NotApplicable,

    /// <summary>The kind is not one of <see cref="KeyMoveApprovalKinds"/>.</summary>
    UnknownKind,
}
