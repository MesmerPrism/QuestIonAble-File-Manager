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

The repository also defines a separate encrypted Kiosk v2 catalog-provider
subprocess. It is not an ADB root profile and does not broaden the file
contract. It is summary-only, read-only, independently optional, and
truthfully unavailable unless File Manager owns an enrolled secure profile.

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
questionable-file-manager-kiosk-v2-provider.exe integration kiosk-v2-catalog --json < <strict-request.json>
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

The dedicated Quest awake provider is a separate contract from this v1
file-transfer hook and from the Kiosk catalog provider. It has no Fleet target,
policy, scheduling, or Manifold authority. Fleet supplies an already accepted
typed request and an exact private serial binding; File Manager returns only
independent effect-owner readbacks. See
[Quest awake control](quest-awake-control.md).

The dedicated Quest connectivity provider is also separate. Fleet supplies an
already-authorized typed action plus logical device binding; File Manager
resolves its own exact USB serial and Kiosk direct profile, then returns only
sanitized setting/listener facts. Kiosk remains the on-device privileged
effect owner and Termux usability remains Fleet-owned signed observation
state. See
[Quest connectivity provider](quest-connectivity-provider.md).
The provider consumes each request and operation ID once per process, but this
is not cryptographic Fleet/Manifold caller authentication or durable replay
state. Its current-user credential profile is readable to another process
running as that Windows user. Production isolation must therefore use a
separate Windows identity until a one-use signed launch capability is part of
the cross-repository contract.

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

The connectivity-provider suite additionally covers strict closed actions,
private profile binding, exact serial-scoped classic setup, setting versus
Meta-prompt/listener separation, absence of Termux claims, sanitized failures,
in-process replay rejection, stable USB/network identity continuity, and
isolated single-file artifact validation with a fresh extraction directory per
launch.

## Encrypted Kiosk v2 catalog provider

`integration kiosk-v2-catalog --json` reads exactly one JSON document from
standard input, capped at 64 KiB, and emits exactly one final JSON document.
Fleet invokes this route only through the reviewed release artifact named
`questionable-file-manager-kiosk-v2-provider.exe`. That artifact is the
self-contained, single-file `win-x64` publish produced directly from
`QuestIonAbleFileManager.FleetKioskV2Provider`; it is never copied or renamed
from the general `QuestIonAbleFileManager.Cli` executable. Its entrypoint has
one exact case-sensitive execution vector,
`integration kiosk-v2-catalog --json`, and one separate exact inert description
vector, `--describe-json`. Description derives only the existing
`kiosk.catalog-summary` scope and returns before stdin, profile, provider, HTTP,
target, or owner-session use. It is target-free and explicitly
non-authorizing; descriptor availability is not Kiosk/backend availability.
All help, ADB, file, APK, Wi-Fi, Kiosk, kiosk-direct, device, other integration,
mixed, missing/extra, reordered, and case-varied shapes return the strict
`provider_arguments_invalid` response before stdin, profile, HTTP, or general
CLI initialization.

Fleet pins the artifact by lowercase SHA-256 before staging the EXE into a new
private launch directory and creating only its empty private `bundle-extract`
subdirectory. A framework-dependent apphost produced by `dotnet build` is not
a provider artifact: its sibling assembly, dependency, runtime-configuration,
and runtime files remain executable trust inputs.
Fleet sets `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to a new private directory inside
each hash-pinned stage and never reuses a shared extraction root. Native runtime
files, when the selected .NET runtime extracts any, therefore remain scoped to
that staged trust unit. The release gate accepts an empty extraction directory
or at most 128 non-reparse files, 16 directories, and 128 MiB there; it rejects
any other top-level stage entry.

`tools/Test-FleetKioskV2ProviderArtifact.ps1` publishes the exact dedicated
project with no arbitrary input-artifact override, rejects sibling
DLL/PDB/dependency/runtime-configuration outputs, stages the EXE with its empty
private extraction directory, first proves description exits while stdin stays
open and ADB/Kiosk settings are poisoned, and then runs an absent-profile
request. The gate requires exit code `3`, byte-exact strict `unavailable` JSON,
zero standard-error bytes, and no unexpected sibling files. It runs the
existing broad rejection set plus mixed, extra, and case-varied description
arguments to prove the general dispatcher is unreachable. It emits a receipt
that binds the dedicated source project, those negative checks, and the exact
lowercase SHA-256 Fleet must pin. The gate also builds the dedicated project's
normal framework-dependent apphost and proves that isolating that apphost does
not operate. The normal release build runs the same gate and places the
executable and receipt in both portable archives.

The request schema is
`questionable.file_manager.fleet_kiosk_v2_catalog_request.v1`:

```json
{
  "schema": "questionable.file_manager.fleet_kiosk_v2_catalog_request.v1",
  "profile_id": "<opaque-file-manager-profile>",
  "request_id": "<kiosk-compatible-request-id>",
  "device_id": "<fleet-device-id>",
  "identity_revision": 1,
  "capability_id": "rusty-kiosk.direct-operator",
  "capability_evidence_revision": 1,
  "route_id": "kiosk.encrypted.v2",
  "expected_owner_epoch": null,
  "scopes": ["kiosk.catalog-summary"],
  "issued_at_ms": 1900000000000,
  "expires_at_ms": 1900000025000
}
```

Unknown, duplicate, trailing, noncanonical, expired, future-skewed, or
longer-than-30-second requests are rejected. The scope must be exactly the
single summary scope. Detail and launch scopes, arbitrary endpoints, Manifold
barriers, shell, paths, and mutations are not fields in this schema.
`expected_owner_epoch` may be omitted or `null` only for first admission. File
Manager then returns the epoch authenticated by the fresh pairing-derived
session proof. Fleet retains it and supplies that exact value on later
refreshes; a changed pinned epoch is rejected rather than silently rebound.

The opaque profile selects the current Windows user's generic Credential
Manager record at
`QuestIonAbleFileManager/RustyKioskV2/<profile_id>`. Its bounded credential
blob uses schema
`questionable.file_manager.rusty_kiosk_v2_profile.v1` and contains only the
same profile ID, one fixed-port HTTP endpoint, pairing code, and stable Fleet
device ID. It does not cache identity revisions, capability revisions, grant
revisions, or Kiosk key/owner/grant epochs. Those epochs and grants are fetched
from and authenticated against Kiosk on every exchange. This repository
provides no CLI credential-import route, so pairing material is never accepted
through process arguments, environment variables, request JSON, or logs.

File Manager checks the exact public v2 protocol constants, pairing proof,
HKDF-SHA-256 schedule, separately derived request/response AES-256-GCM keys,
random directional nonce prefixes, strictly monotonic counters, canonical
base64url, AAD, request digest, time windows, and current summary grant. The
encrypted catalog request is Kiosk's fixed
`rusty.kiosk.catalog_request.v2`. V1 fallback and app launch are rejected.

Success uses
`questionable.file_manager.fleet_kiosk_v2_catalog_response.v1` with
`status: verified`. It maps exactly to Fleet's verified catalog exchange:
bounded base64url request and response envelope bytes, the deliberately
exportable owner summary JSON, the authenticated session/grant receipt, and
their exact request/device/identity/owner/scope/time bindings. File Manager
does not export the endpoint, pairing material, derived keys, session
plaintext other than the owner-approved catalog evidence, or decrypted
transport metadata. The numeric `grant_revision` is a deterministic nonzero
binding identifier derived from the freshly authenticated summary-grant epoch;
it is not an ordering counter or cached authority.

Failure uses only `schema`, `status`, `profile_id`, `request_id`, and a stable
`error_code`. It contains no free-form remote response or exception text.
The subprocess status and process exit code are one exact contract:
`verified` maps to `0`, `failed` to `1`, `rejected` to `2`, and `unavailable`
to `3`. Standard error is always empty for those structured results. Fleet
must parse and bind the JSON status to that exact exit code; it must not treat
all nonzero exits as unstructured provider failure. Cancellation is reported
as structured `failed` with exit code `1`, not the unrelated v1 integration
adapter's exit code `4`.
Decoded bounds are 1 MiB for the raw request envelope, 2 MiB for the raw
response envelope, 768 KiB for owner catalog evidence, and 64 KiB for the
grant receipt; the base64-expanded final JSON is capped at 8 MiB. The encrypted
request/response itself retains Kiosk's
30-second maximum while the owner snapshot keeps its independently
authenticated five-minute search TTL. Fleet retains and rechecks its Manifold
barrier locally; it is never sent to File Manager.

Host tests use a fixed vector from Kiosk's canonical/KDF/AAD/nonce rules and
exercise a detail-granted public contract with a summary-only session,
five-minute owner freshness, proof/digest/nonce/counter/ciphertext tampering,
strict parsing, request freshness, profile/device mixups, deterministic framed
replay identity, evidence clearing, and secret-free failures. Physical
headset validation remains an attended step.
