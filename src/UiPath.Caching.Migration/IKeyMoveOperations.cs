using UiPath.Caching.KeyMove;

namespace UiPath.Caching.Migration;

/// <summary>What an operator does to a key move group: read its status, initialize it, hold and resume it, store an approval, and run its dry run.</summary>
/// <remarks>
/// Registered by <c>UseKeyMove</c> with at least one group. A group the application did not register throws <see cref="ArgumentException"/>. Every call works on the group's state in Redis, so it gives the same answer on every node of the application;
/// the dry run scans from the node it runs on. The stage itself follows the stage flag of <c>UseKeyMove</c>: these calls never move a group to another stage.
/// </remarks>
public interface IKeyMoveOperations
{
    /// <summary>Reads the stored state of <paramref name="group"/>.</summary>
    /// <returns>Null when the group has no key move state yet.</returns>
    Task<KeyMoveStatus?> GetStatusAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>Creates the key move state of <paramref name="group"/> in <see cref="KeyMoveStage.NotStarted"/> with the caches its registration names; a state still in that stage takes the registration's caches again.</summary>
    /// <returns>False when the group is past <see cref="KeyMoveStage.NotStarted"/>.</returns>
    Task<bool> InitializeAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>Holds the group at its stage until <see cref="ResumeAsync"/>; a step back to the in-place keys still runs.</summary>
    /// <param name="group">The group.</param>
    /// <param name="reason">Why, as it appears in the status and the alerts.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>False when the group has no state.</returns>
    Task<bool> HoldAsync(string group, string reason, CancellationToken cancellationToken = default);

    /// <summary>Clears the operator's hold and a hold the move set itself.</summary>
    /// <returns>False when the group has no state.</returns>
    Task<bool> ResumeAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>Stores an approval for the group's current state; it is void once the state moves or the evidence it covers changes.</summary>
    /// <param name="group">The group.</param>
    /// <param name="kind">One of <see cref="KeyMoveApprovalKinds"/>.</param>
    /// <param name="confirm">For a kind that goes past a check, exactly <see cref="KeyMoveApprovalKinds.ConfirmTextOf"/>; otherwise a short free text, kept with the approval.</param>
    /// <param name="operatorId">Who approves, kept with the approval.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentException">The confirm text is not the one the kind needs, or a text is empty.</exception>
    Task<KeyMoveApproval> ApproveAsync(string group, string kind, string confirm, string operatorId, CancellationToken cancellationToken = default);

    /// <summary>Runs the dry run of the group and stores its report, which the start of a move needs.</summary>
    /// <param name="group">The group.</param>
    /// <param name="reverse">Counts the native keys for a step back instead; the start of a move does not take that report.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <exception cref="InvalidOperationException">The router's library has not started, or the group has no state.</exception>
    Task<KeyMoveDryRun> RunDryRunAsync(string group, bool reverse = false, CancellationToken cancellationToken = default);
}
