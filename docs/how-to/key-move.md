# Operating a key move

A key move copies the keys of a group from their in-place form to the native keys of the library, then retires the in-place keys. This page covers running a move once `UseKeyMove` is registered (see [Routing between an existing Redis client and the library](router.md)).

Two things drive a move:

- The **stage flag** of the group, which the `IMigrationStageSource` given to `UseKeyMove` reads. It says how far the group may go.
- The **operations** of `IKeyMoveOperations`, which store the approvals, holds and dry run the gates ask for.

The leader node steps the group. Nothing an operator calls moves a group to another stage by itself.

## Get the operations

`UseKeyMove` with at least one group registers `IKeyMoveOperations` as a singleton. Every call reads or writes the group's state in Redis, so any node gives the same answer.

```csharp
var operations = services.GetRequiredService<IKeyMoveOperations>();

await operations.InitializeAsync("orders");
var report = await operations.RunDryRunAsync("orders");
await operations.ApproveAsync("orders", KeyMoveApprovalKinds.Start, "release 24.10 or later on every node", "jane.doe");

var status = await operations.GetStatusAsync("orders");
// status.Stage, status.BlockingGates, status.BlockedSince, status.Approvals ...
```

A group the application did not register throws `ArgumentException`. A call before the library starts, or after it stops, throws `InvalidOperationException`.

## Stage flag

Use `StringFlagStageSource` with a string flag getter. It accepts `off`, `copying`, `copied`, and `done`, case-insensitively, and ignores surrounding whitespace.
The numeric strings `0` through `5` remain accepted. `IntFlagStageSource` continues accepting integer flags.

```csharp
var stages = new StringFlagStageSource((group, fallback, cancellationToken) =>
    ValueTask.FromResult<string?>(configuration[$"KeyMove:{group}:Stage"] ?? fallback));
```

Names map to the corresponding stage numbers. The existing advancement and rollback rules remain unchanged.

| Flag | The group goes to |
| --- | --- |
| `off` (`0`), `1`, `2` | Off. From Preparing or Switching it goes back at once; from Copying or Copied it moves the keys back first. |
| `copying` (`3`) | Copying, through Preparing and Switching. From Copied it goes back to Copying. |
| `copied` (`4`) | Copied, once the mover finds nothing left to move. |
| `done` (`5`) | Done, once the close gates pass and the close approval is stored. |

Preparing and Switching remain automatic intermediate stages. The flag names `preparing` and `switching` are rejected. Numeric flags `1` and `2` still mean off for compatibility.
Reverse stage names and all former stage names are also rejected as flags.
A missing, empty, or unknown flag retains the last valid view. Before any valid read, the node advertises no flag view.
A flag below `done` on a done group only raises the `flag_below_closed` alert.

## Stages

`GetStatusAsync` returns the stage as `KeyMoveStage`.

| Stage | What happens | Check before you go on | Approvals |
| --- | --- | --- | --- |
| `Off` | Every cache reads and writes its in-place keys. | Run `InitializeAsync`, then `RunDryRunAsync`. Read the dry run's counts, size buckets, memory estimate and duration. | `start` if the release floor was not attested with `AttestReleaseFloor()`. `start_unsettled`, `start_no_dry_run`, `start_memory` or `control_records_evictable` when a start check fails for a known reason. |
| `Preparing` | Every node writes the move's forms; the in-place side is still the authority. | All nodes acknowledge it. | None. |
| `Switching` | Caches without an origin close their writes while the last in-place writers drain. | Switching ends on its own. | `skip_barrier` while a transition is still pending and a replica never confirms it. |
| `Copying` | The native side is the authority. Reads promote keys, and the mover copies the rest. | Watch the mover's progress and `BlockingGates`. | `force_reverse` to step back past a failing reverse gate. |
| `Copied` | The mover found no key left. Reads still fall back to the in-place keys for the residue. | Read `Residue`. Wait for the soak. Keep the code that reads the in-place keys deployed. | `close` or `close_force` covers the residue. `close_force` also allows closing before the soak or with canary keys. |
| `Done` | The group uses the native keys only, and the leftover in-place keys are swept. | Nothing. | `abandon` to start every cache cold. `skip_lswept_wait` can be stored at this stage and has no effect on the sweep. |

An approval holds for the state it was stored in. It is void once the state moves, or once the evidence it covers changes, such as the residue of a `close`. `Approvals` in the status lists only the valid ones.

## Confirm texts

An approval that goes past a check needs this exact text as `confirm`. Any other text throws `ArgumentException`. `KeyMoveApprovalKinds.ConfirmTextOf(kind)` returns it. Every other kind takes a short free text, kept with the approval.

| Kind | Confirm text |
| --- | --- |
| `close_force` | `close without the full soak or with canary keys counted` |
| `force_reverse` | `reverse although a node does not meet the reverse gates` |
| `skip_barrier` | `go on although a replica did not confirm the barrier` |
| `abandon` | `abandon the move and start every cache of the group cold` |
| `start_unsettled` | The `UpgradeHoldOverrideText` of the stage driver options. |

## Holds

`HoldAsync(group, reason)` keeps the group at its stage until `ResumeAsync`. A step back still runs while a group is held. The move also holds itself, for example when a node without key move support joins or canary keys are counted. `AutomaticHold` shows that reason, and `ResumeAsync` clears both kinds.

## Why a group waits

When the leader's last step took no step, `BlockingGates` lists each reason as `code:detail`. The detail names the failing gate IDs, for example `start_blocked:start.dry_run,start.release_floor`. `BlockedSince` is when the leader first saw those reasons. Both clear once a step is taken.

Common codes:

- `start_blocked`: a start gate fails.
- `stage_pending`: the stage waits for the mover, such as `copied:drain.confirming_pass`.
- `close_pending`: a close gate fails.
- `reverse_blocked`: a reverse gate fails.
- `held`: an operator or automatic hold.
- `gate_override`: a stored approval went past a gate.

## Rolling back

| From | How | What happens |
| --- | --- | --- |
| `Off` | Nothing to do. | Nothing was written. |
| `Preparing`, `Switching` | Set the flag to `off`. | The group returns to `Off`. Nothing was moved. |
| `Copying`, `Copied` | Set the flag to `off`. If a reverse gate fails, approve `force_reverse`. | The group steps back with `Reverse` set. It walks `returning`, `copying-back` and `copied-back` while the mover copies the native keys back. Approve `release` at `copied-back` to sweep the native keys and return to `off`. If old markers block the step back, `retire_markers` lets it go on. |
| `Copied` | Set the flag to `copying`. | The group returns to `Copying`. |
| `Done` | No step back. Approve `abandon` for caches that accept a refill. | Every cache of the group starts cold on its native keys. |

With `Reverse` set, enum values `Preparing`, `Copying` and `Copied` display as `returning`, `copying-back` and `copied-back`, respectively.
A released reverse group returns to `Off`, displayed as `off`.

Keep the code that reads the in-place keys deployed while a step back is possible. The `close.consent` gate checks for a valid close approval covering the stored residue and checks that caches without an origin accept a refill if they have residue. A step back from `Copying` or `Copied` needs that code, so attest that it is still deployed with `AttestInPlaceReadersDeployed(group, evidence)` on the key move builder. The `close.in_place_readers` gate refuses to close without it, and no approval overrides that gate.

## Telemetry and gate IDs

The meter `UiPath.Caching.KeyMove` has these counters:

- `cache.keymove.gate_overrides`, tagged `group` and `gate`: a stored approval let a step past a gate.
- `cache.keymove.canary_keys`, tagged `group`: keys counted as written after the switch during a forward move. This metric is forward-only.
- `cache.keymove.marker_orphans`, tagged `group`: markers that no generation owns.

`UseKeyMove(..., moves => moves.OnAlert(...))` receives every alert, with the codes above. The gate IDs are listed in [Key move stage checks](router.md#key-move-stage-checks). The same list also has `close.in_place_readers`.
