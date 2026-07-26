# Optional Rusty Fleet Integration

## Decision

Expose a disabled-by-default, versioned subprocess contract for one exact ADB
device at a time. Rusty Fleet may discover the contract, observe one serial,
and request a read-only shared-storage listing or one staged file pull. The
Core additionally implements bounded no-overwrite push, but advertises it only
when its host injects a current Quest-identity and Manifold mutation-authority
verifier. The ordinary environment-created CLI adapter remains read-only.
QuestIonAble File Manager remains the owner of ADB discovery, the `/sdcard`
root mapping, local staging, path validation, transfer evidence, and the final
JSON result.

This is an optional adapter. File Manager starts and all ordinary file, APK,
Kiosk, device, and Wi-Fi features keep working when Fleet is absent. Fleet does
not need this adapter for monitoring or non-file operations.

## Enablement

Integration is inert until an operator supplies both settings:

```powershell
$env:QUESTIONABLE_FILE_MANAGER_FLEET_INTEGRATION = 'enabled'
$env:QUESTIONABLE_FILE_MANAGER_FLEET_ADB_SHARED_ROOT = '<absolute-owner-approved-staging-root>'
```

The staging root must already exist and must not be a symbolic link, junction,
or other reparse point. Its Windows ACL must restrict create, delete, and rename
rights to the operator/File Manager security context; handle ownership blocks
in-operation substitution but cannot make a replay tombstone durable against a
different principal that may delete arbitrary children after File Manager
closes them. Fleet must treat this root as File Manager-owned state and must not
remove `.lock` tombstones or completed operation directories. Capability
discovery never creates the root, starts ADB, or opens the WPF application.
Removing the enable setting returns the adapter to `disabled` without changing
standalone File Manager behavior.

These settings enable only list, pull, and read-only durable status in the
shipped CLI. They cannot enable push or cancellation. A host that embeds the
Core must inject `IFleetMutationAuthorityVerifier`; the verifier, not request
fields or environment variables, decides whether the exact Quest proof,
command, lease, provider epoch, and revocation barrier are current.

The capability state is explicit:

| State | Meaning |
| --- | --- |
| `ready` | Opted in, staging root is safe and present, and ADB was located. |
| `disabled` | Operator opt-in is absent or explicitly disabled. |
| `absent` | Opt-in is enabled, but the approved staging-root setting or directory is absent. |
| `unsupported` | The requested contract version or configured mode is unknown. |
| `unavailable` | ADB or the configured root cannot currently be used. |

Capability discovery returns exit code zero even when the capability is not
ready because discovery itself succeeded. The JSON `success` and `status`
fields carry the feature state.

## CLI

All integration routes require `--json` and emit exactly one final JSON
document on standard output:

```powershell
questionable-file-manager.exe integration capabilities --json
questionable-file-manager.exe integration observe --serial <quest-serial> --json
questionable-file-manager.exe integration invoke --request <operation-request.v1.json> --json
questionable-file-manager.exe integration status --operation <operation-id> --json
```

Pass `--contract-version 1.0` when the caller wants an explicit negotiation
check. Pass `--adb <path-to-adb>` to bind the adapter epoch to an exact ADB
executable without changing global ADB state.

Exit codes are:

- `0`: capabilities were returned, an observation was issued, or an operation
  completed;
- `1`: execution failed;
- `2`: input, schema, version, or operation was rejected;
- `3`: disabled, absent, unavailable, or unauthorized;
- `4`: cancelled.

Errors use the same `questionable.file_manager.integration.response.v1`
envelope as successful results. They contain a stable error code, message, and
retryable flag. Integration errors do not mix console prose or progress events
into standard output.

## Contract v1

The v1 schema family is:

- `questionable.file_manager.integration.response.v1`;
- `questionable.file_manager.integration.capability_snapshot.v1`;
- `questionable.file_manager.integration.device_observation.v1`;
- `questionable.file_manager.integration.device_binding.v1`;
- `questionable.file_manager.integration.mutation_authority.v1`;
- `questionable.file_manager.integration.operation_request.v1`;
- `questionable.file_manager.integration.operation_result.v1`;
- `questionable.file_manager.integration.operation_status.v1`.

The contract version is exactly `1.0`. Request JSON rejects comments, trailing
commas, duplicate fields, unknown fields, excessive nesting, documents over
64 KiB, unsupported schema/version values, and operation-specific fields on
the wrong operation.

`integration observe` rediscovers the exact requested ADB serial and records
its ready state and USB/Wi-Fi transport. An operation request carries the
returned adapter epoch, observation digest, serial, transport, observation
time, and an expiry no more than five minutes in the future. Observations are
valid for at most two minutes. The adapter rediscovers the exact serial before
and after each operation; absence, unauthorized state, offline state, or
transport mismatch rejects the operation instead of falling back to another
headset.

For every list or pull, one owner-issued Android shell command resolves both
`/sdcard` and the requested path. The requested canonical path must equal the
canonical root plus the validated lexical relative path exactly. The command
then opens file descriptor 3, resolves `/proc/self/fd/3`, and repeats that exact
comparison before listing or streaming through the descriptor. A symlink,
intermediate indirection, canonical escape, path swap before open, wrong file
kind, or unavailable proof fails closed.

An operation request has this shape:

```json
{
  "schema": "questionable.file_manager.integration.operation_request.v1",
  "contractVersion": "1.0",
  "requestId": "<unique-request-id>",
  "operationId": "<unique-one-use-operation-id>",
  "adapterEpoch": "<capability-adapter-epoch>",
  "expiresAtUtc": "<iso-8601-round-trip-time>",
  "deviceBinding": {
    "schema": "questionable.file_manager.integration.device_binding.v1",
    "observationId": "<observation-digest>",
    "serial": "<quest-serial>",
    "transport": "usb",
    "observedAtUtc": "<observation-time>"
  },
  "operation": {
    "kind": "list",
    "rootProfile": "adb-shared",
    "relativePath": "Download",
    "maximumEntries": 100
  }
}
```

A pull replaces the operation object with:

```json
{
  "kind": "pull",
  "rootProfile": "adb-shared",
  "relativePath": "Download/example.bin",
  "maximumBytes": 104857600
}
```

An injected-authority host may use push with a source already staged below the
approved root:

```json
{
  "operation": {
    "kind": "push",
    "rootProfile": "adb-shared",
    "relativePath": "Download/example.bin",
    "maximumBytes": 104857600,
    "localArtifactPath": "artifacts/<artifact-id>/payload.bin",
    "expectedSizeBytes": 12345,
    "expectedSha256": "<lowercase-sha256>"
  },
  "mutationAuthority": {
    "schema": "questionable.file_manager.integration.mutation_authority.v1",
    "fleetDeviceId": "<fleet-device-id>",
    "fleetIdentityRevision": 1,
    "questIdentityProofId": "<quest-proof-id>",
    "manifoldCommandId": "<command-id>",
    "manifoldLeaseId": "<lease-id>",
    "manifoldProviderEpoch": "<provider-epoch>",
    "revocationBarrierRevision": 1,
    "expiresAtUtc": "<time-no-later-than-request-expiry>"
  }
}
```

Caller-supplied authority fields are evidence inputs, not acceptance. The
injected verifier must accept them before staging, immediately before the
stream, and after exact-serial readback. All three decisions must return the
same verified authority digest. A revocation token and an absolute deadline
250 ms before the earlier request/authority expiry stop the process tree; a
late copy is never automatically retried.

## Path And Transfer Safety

`adb-shared` is the only v1 route profile and maps to `/sdcard`. The caller
supplies a normalized relative path, never an absolute Android or Windows
path. File Manager rejects:

- leading/trailing separators, empty segments, `.` or `..`;
- control characters and Windows-invalid characters;
- trailing spaces or periods and reserved names such as `CON` or `LPT1`;
- paths over 512 characters or segments over 128 characters;
- unsupported root profiles and unsupported operation kinds.

List enumerates the descriptor-owned directory with a remote counter before
host materialization and fails rather than silently truncating when the
requested limit is exceeded. The remote enumerator accepts only regular files
and directories; symbolic links, special entries, and control characters in
names fail closed instead of being projected as ordinary files. Pull checks the
opened descriptor's remote size before transfer and then uses bounded binary
`adb exec-out` streaming. The host reads at most `maximumBytes + 1`, kills the
ADB process tree on the extra byte, and never writes beyond `maximumBytes`.
Changing or growing the remote file after its size check therefore still hits
the host hard stop.

Pull writes only to:

```text
<approved-root>/operations/<operation-id>/payload.bin
```

Pull operation IDs are one-use. A reservation closes the creation race. On
success, the retained operation directory makes replay a collision; on
failure, the empty reservation is retained as a replay tombstone. Windows
handles keep the approved root, `operations`, operation directory, reservation,
and final output identity-locked without delete sharing through the write. The
adapter validates the final handle path, reparse state, delete-pending state,
and single-link count immediately before and after streaming. It computes size
and lowercase SHA-256 from the same bounded byte stream and verifies that the
still-open owned file has that exact length.

Failure cleanup marks only the owned output and operation-directory handles for
deletion, then closes the retained reservation tombstone. It never recursively
follows a path, so a swapped junction cannot redirect cleanup. If an adversarial
local actor renames the just-created operation directory before File Manager
can acquire its handle, the operation fails closed and may leave that empty,
unowned directory for operator cleanup; File Manager will not risk deleting it
by path. Ctrl+C cancels the linked operation token, kills the subprocess, and
runs the same owned-handle cleanup. Timeouts return a retryable
`operation_timeout` error and also clean the operation-owned payload staging.

Push sources are limited to
`artifacts/<id>/payload.bin` or a completed
`operations/<id>/payload.bin`. File Manager opens and retains every Windows
ancestor plus the source handle without write/delete sharing, rejects reparse
points, delete-pending state, hardlinks, size drift, and digest drift, then
hashes the bytes again from that same handle while writing ADB standard input.

The remote command canonicalizes the exact `/sdcard` parent, creates a unique
operation partial with shell no-clobber semantics, validates its descriptor
through `/proc/self/fd`, and verifies size/SHA-256 while the final name remains
absent. It atomically hard-links that verified inode to the final name without
replace, repeats descriptor-bound size/SHA/inode readback, and removes only the
operation-owned partial name. Destination collisions are rejected without
overwrite. A filesystem that cannot provide the atomic hard-link primitive
fails closed. It never overwrites, deletes an existing target, or falls back
to `adb push`.

Push reserves `<operation-id>.lock` with the full request and authority digest
before creating its append-only journal. Journal entries use contiguous
sequences and must preserve the reservation's request digest, target, source,
serial, authority, and expected content exactly. An OS-enforced share-zero
`owner.live` handle proves that a process is still active without relying on a
PID. When the handle is no longer locked and no terminal journal exists,
read-only status reports `recoveryRequired`; it separately reports
`destinationMayExist` and `partialMayExist`. Recovery never retries a mutation
or cleans a remote path automatically. Cancellation is available only through
the injected-authority Core API, which revalidates the reservation's exact
authority digest before writing the durable cancel marker.

## Non-scope And Remaining Authority

The ordinary environment/CLI adapter has no push or cancellation authority.
The injected Core route has no delete, move, rename, overwrite, multi-target
route, ADB daemon lifecycle, Fleet scheduling, WPF automation, background
service, installer, or Kiosk-staging substitution. Fleet remains the only
owner of 10–100-device batching.

The v1 observation proves File Manager transport continuity; it does not prove
that an ADB serial is a Rusty Fleet device identity. Push therefore remains
unadvertised unless a separate owner supplies the current Quest-owned identity
proof and Manifold command/lease/revocation verifier.

## Validation

Unit tests cover explicit capability states, side-effect-free disabled
discovery, exact serial observation, pre/post rediscovery, strict JSON,
unsupported mutation rejection, traversal and reserved-name damage, bounded
listing, canonical remote escape rejection, hard-capped streaming, coherent
staged pull hashing, collision rejection, hardlink/final-file/parent-junction
race defense, cancellation/timeout cleanup, stale binding rejection, and loss
of the exact serial after transfer. Push tests cover disabled advertisement,
staged-input containment, same-stream hashing, no-overwrite construction,
exact serial before/after, authority digest continuity, revocation and expiry
during streaming, cancellation authorization, live/dead owner status,
reservation/journal substitution, truthful uncertain cleanup, and one-use
replay. Repository
build, test, CLI help, public-boundary, branding, and asset checks remain the
normal source gates. Live headset validation is separate and is not implied by
these host tests.
