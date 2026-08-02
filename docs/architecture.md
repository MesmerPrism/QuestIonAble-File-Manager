# Architecture

## Decision

Use a dependency-light .NET core for both serial-scoped ADB operations and the
bounded Rusty Kiosk direct protocol, a CLI for complete automation parity, and
a thin Windows WPF projection. This is a focused file/APK/Kiosk operator, not a
general Quest runtime console or fleet manager.

## Scope

- ADB tool discovery and bounded process execution.
- Serial-scoped device discovery and operations.
- Browsing, pulling, and explicit pushing on shell-accessible paths.
- Third-party package listing, single-APK export, inspected/hash-bound
  single-APK install, constrained resolved launch, structured runtime
  observation, and atomic folder-based split APK set install.
- Explicit Wi-Fi ADB enable/connect/disconnect with no ADB daemon lifecycle.
- Bounded parallel single-APK and complete split-set installation across
  distinct Wi-Fi ADB endpoints, with one result per target.
- Windows GUI and CLI projections.
- Optional inert dedicated loopback API projection over a retained,
  one-use subset of the same typed command registry.
- Optional single-headset Rusty Kiosk direct transport for typed Kiosk control,
  fixed tags, app-owned staging, and attended PackageInstaller sessions.
- Optional disabled-by-default Rusty Fleet interop for one exact serial,
  bounded shared-storage list/pull, durable status, and Core-only no-overwrite
  push when current Quest/Manifold authority is injected.
- Optional dedicated Fleet awake effect-owner provider for exact-serial
  bounded holds, drift-only repair, a temporary device watchdog, watchdog
  stop, and explicit normal-settings restore.
- Optional dedicated Fleet connectivity provider for File Manager-owned
  private target resolution, fixed Kiosk Wi-Fi ADB setup requests, and a
  separate exact-USB classic TCP/IP route.
- Shared inert capability descriptions for the three dedicated providers,
  derived from their existing typed registries without backend initialization.
- Optional distribution-only verification and handoff of one configured,
  signed Rusty Fleet Windows guided installer release.
- Public CI, Pages, release archives, and boundary validation.

## Non-scope

- Protected app-data access, rooting, entitlement bypass, or DRM handling.
- App data, saves, OBB, or asset-pack backup.
- General remote-path deletion, package uninstall, clear-data, or ADB daemon
  lifecycle. Deletion inside Rusty Kiosk's explicitly bounded app-owned staging
  area is supported.
- TLS, network scanning, fleet discovery, online relays, or multi-device direct
  orchestration.
- Fleet target scheduling, Fleet identity inference, multi-target file
  operations, or mutation based only on caller-supplied Fleet fields.
- Fleet installation semantics, runtime configuration, device enrollment,
  connectivity, hotspot control, credential exchange, or arbitrary
  bootstrapper execution.
- Bundled Android tools, APK catalogs, private packages, or live evidence.
- Android and Apple host applications in the first release.

## Authority

Android's package manager owns installed package paths. ADB owns transport.
`QuestIonAbleFileManager.Core` owns safe command construction, parsing, transfer
policy, and export completeness checks. The app and CLI adapt user intent into
that core and do not redefine behavior.

The inspected-deployment boundary derives identity from one local APK, confirms
installation through exact serial/package/version/signer/base-APK byte
readback, resolves only an exported launcher activity, and observes only bounded
package/activity/process state. See `inspected-deployment.md`.

The dedicated local API owns no device semantics. Core owns its strict
preflight/retain/consume/status/cancel state machine and exact command digest;
the API executable owns only explicit loopback binding, bearer admission,
bounded HTTP bodies, and structured projection. The API is Windows-only.
Private create-new staging, retained ancestor/directory/file handles, bounded
HMAC journal plus monotonic external anchor, replay tombstones, serialized
capacity reservations, journal-before-delete cleanup debt, staged-byte
reinspection on recovery, and truthful outcome-unknown recovery are part of
that Core authority. The anchor detects stale-journal replacement but makes no
claim against compromise of the same Windows user and secret. See
`local-api.md`.

Wi-Fi enablement is a sequenced transaction: read one stable identity and
`wlan0` from one USB serial, run `tcpip` on that same serial, connect one
validated endpoint, verify its ready device row, and require the same identity
through that exact endpoint. Connection establishment is endpoint-scoped
because no ADB serial exists before `connect`; all subsequent device work is
serial-scoped.
Parallel installation owns only bounded orchestration. Android package-manager
transactions remain independent per headset.

Rusty Kiosk owns catalog/tag semantics, normal versus guarded launch, the
Accessibility watchdog, user-facing opt-ins, app-owned staging, and Android
PackageInstaller sessions. The file manager owns ADB fallback/bootstrap and the
desktop/CLI projection. ADB host control crosses only Kiosk's exported
`android.permission.DUMP` provider. Direct control crosses schema
`rusty.kiosk.direct_operator.v2`: expiring HMAC requests, persisted replay IDs,
signed readbacks, fixed endpoints, bounded filenames, and no shell, component,
intent, or arbitrary path input.

For the optional Fleet adapter, File Manager owns the `adb-shared` mapping to
`/sdcard`, exact ADB serial observation, adapter epoch, relative-path policy,
operation staging, subprocess execution, and result evidence. Fleet owns
device identity and every batch. A File Manager observation is transport
evidence only and cannot be relabeled as Fleet identity proof.

The separate Fleet awake provider owns only exact-serial ADB effects and fresh
power/watchdog readback. Fleet owns immutable target selection, confirmation,
Manifold authority, Windows watchdog scheduling, and per-target operation
state. The provider never receives Fleet policy authority and never returns
the private serial, controller identifiers, raw ADB output, or caller-defined
shell. Stop-watchdog and restore-normal remain separate commands. See
[Quest awake control](quest-awake-control.md).

The separate Fleet connectivity provider resolves one Fleet device ID through
File Manager's current-user secure profile. Kiosk remains the on-device
privileged effect owner for modern Wireless Debugging settings and after-boot
requests. File Manager separately owns the exact-USB classic `tcpip 5555`
sequence. Fleet never supplies or receives the profile's serial, endpoint, or
pairing code. The receipt keeps Kiosk setting, Meta wearer approval, listener
discovery, and effect readback independent. Termux usability belongs to
Fleet's separate signed observation state and is absent from this receipt. See
[Quest connectivity provider](quest-connectivity-provider.md).
The provider rejects duplicate request and operation IDs within one process,
but its request has no cryptographic Manifold caller proof. Current-user
Credential Manager does not isolate secrets from another same-user process;
deployment uses a separate Windows identity when that caller boundary matters.
Classic TCP/IP also binds the pre-mutation USB identity to fresh readback from
the exact connected endpoint so a stale endpoint cannot be relabeled.
The ordinary File Manager process, never the dedicated provider, owns profile
status, sanitized inventory, strict private enrollment/replacement, and
explicit revocation. CLI file/stdin and WPF controls share the same typed Core
commands and credential-store abstraction. The WPF may bind its selected exact
USB device and already-entered Kiosk direct link through the standard-input
route entirely in memory; it does not persist those fields in ordinary
settings or a temporary file, and clears the transient endpoint/password
controls for every outcome. A create-only request must receive an
existing-record response before a distinct replacement confirmation is
offered. Profile lifecycle receipts expose no serial,
endpoint, pairing code, Credential Manager target, or inferred connectivity.
An exact post-write readback and parse is the commit point. Failed create
verification deletes and confirms absence; failed replacement verification
restores and confirms the retained exact prior blob. An unverified rollback is
reported as uncertain rather than relabeled as a successful mutation.

`ProviderCapabilityDiscoveryProjection` owns only the strict DTO projection of
those three existing provider registries. Awake and connectivity action lists
come directly from `QuestAwakeContract.Actions` and
`QuestConnectivityContract.Actions`; Kiosk exposes only
`CatalogSummaryScope`. Connectivity is split into Kiosk-owned wireless and
File Manager-owned classic TCP/IP capability records so discovery does not
move effect ownership. The descriptor is fresh for five minutes, target-free,
and explicitly non-authorizing. It carries no invocation, path, endpoint,
credential, profile value, target, backend status, or execution receipt.
Provider version is read from a dedicated immutable Core assembly metadata
value generated from `$(Version)`, then checked against the shared contract's
strict lowercase semantic-version grammar before any descriptor is emitted.

The Fleet installer handoff owns only release-consumer verification: a pinned
descriptor RSA key and channel, strict v2 signed payload, exact immutable
GitHub Release URL/version plus installer size/hash/name/protocol, pinned
Authenticode signer, a private retained stage, replay/downgrade state, and
Fleet's fixed non-mutating plan contract. The canonical MesmerPrism Pages path
owns metadata only; it is never an installer origin. Published builds use
release-owned embedded trust metadata present only in reviewed checked-in
source on the exact clean tagged release commit. MSBuild, environment,
release-script, and generated-object inputs cannot add trust. Signed payloads
must be RFC 8785 JCS, carry an exact duration that binds issue and expiry, and
expire within 24 hours. Replay state has both a root-bound sibling file anchor
and an elevated signed-Setup-provisioned, administrator-write HKLM record.
That protected record owns the descriptor IDs and version high-water mark.
Core cannot write it directly: a transition is routed to the protected,
self-pin-verifying Setup copy, which re-verifies the current signed descriptor
before a monotonic update. Descriptor fetch through exact durable readback is
one machine-wide transaction under a protected SYSTEM/Administrators-only
mutex shared with provisioning/repair; abandoned-lock recovery re-reads state
before deciding. Repair can reconstruct mutable local files only from that
protected record and preserves its root digest, accepted IDs, and high-water
mark. A missing protected record is never reconstructed from local evidence;
only the separately named destructive-reset route may explicitly discard
history. Setup updates its protected helper atomically only when the retained
old and new artifacts share the reviewed signer pin. It confirms the exact
prior backup identity, hash, and signer, restores through a separate candidate,
and reopens and revalidates the restored destination before reporting rollback
success. Unknown or failed rollback retains the backup for inspection; one
that completed prior-commitment validation remains exact repair evidence.
An atomic-replace error is treated as potentially state-changing: destination,
replacement, and backup are reconciled against both retained commitments, and
an exact prior backup is never deleted on unknown failure. If rollback's own
atomic restore throws, Setup likewise classifies the destination, rollback
candidate, and original backup; it retains unresolved evidence and cannot
report restoration until stable destination readback matches the candidate
commitment exactly. Signer changes remain rejected without a reviewed rotation
route. Elevated installer inputs
use unpredictable protected Program Files staging with reparse rejection and
per-run cleanup. Missing or
coordinated-deleted files and machine-record loss after initialization fail
closed. Runtime embedded trust ignores developer
environment overrides. Fleet's guided installer owns installation
behavior and any visible Windows consent. Neither the WPF nor CLI can choose a
source, executable, argument vector, credential, device, ADB action, network
action, or elevation mode. See [Fleet installer handoff](fleet-installer-handoff.md).

The shipped CLI has no push or cancellation authority. A Core host may
advertise push only when it injects a verifier for the current Quest identity
and Manifold command, lease, provider epoch, and revocation barrier. The same
verified digest must survive admission, the immediate pre-stream recheck, and
post-transfer exact-serial readback. Expiry and revocation are active
cancellation sources, not post-hoc annotations.

## Interfaces

`ICommandRunner` is the external-process boundary. `AdbClient` exposes device,
file, and package routes. `OperatorCommand` is the shared human-operator
contract: its immutable inputs produce both the CLI argument vector and the
core execution request. Arguments remain structured until they reach the
process API. Remote shell paths use one audited POSIX quoting helper.

`OperatorProgress` is a separate optional projection contract. Core operations
own honest work units; WPF displays them without changing command authority.
Zero total units means indeterminate. CLI JSON remains a stable final result
document and is not interleaved with transient progress events.

`OperatorMutationReceipt` is the result contract for mutations. Its operation
identity, desired state, observed state, transition history, and readback flag
are shared by WPF and CLI. Dispatch records `sent`; the operation then remains
`pending`; only command-specific evidence can produce `confirmed`. A Wi-Fi
prompt request remains pending even when Kiosk reports `adb_wifi_enabled`;
that setting does not prove wearer acceptance, a current listener, or Termux
loopback shell authority. Five-minute non-matches become timed out but remain
reconcilable on later refresh.
Direct commands use the same desired/effective-state matcher. Direct file
mutations confirm only after signed byte/hash readback; local installs stay
pending until the matching Android receipt reports installed or failed.

Each dedicated provider host admits `--describe-json` as one exact,
case-sensitive vector separate from its existing execution vector. That branch
runs before stdin, controller/provider factories, profile stores, ADB, Kiosk,
HTTP, targets, replay state, or other backend use. Mixed, case-varied, or extra
arguments use the existing fail-closed execution rejection and cannot select
description.

The CLI is the contract surface for agents and future GUI, Android-host, and
Apple-host adapters. Any new GUI action must first have an equivalent typed
command, CLI route, optional PowerShell rendering for tests/docs, and parity
test. Automation details stay out of the non-technical WPF interface.

`FleetInstallerHandoff` is the exception to ADB dependence, not to typed-route
parity: `FleetInstallStatus` and confirmed `FleetInstall` are immutable
`OperatorCommand` values shared by WPF and CLI. Its receipts are deliberately
sanitized and contain release identity/evidence only—never URLs, paths,
credentials, process arguments, or device state.

Fleet interop uses strict `questionable.file_manager.integration.*.v1` JSON
over the CLI subprocess. Capability discovery is side-effect free. Observe
binds one ready serial and transport; invoke accepts only one short-lived
observation-bound `list` or `pull`. The adapter rechecks the exact serial before
and after work. The remote owner command resolves `/sdcard`, requires exact
canonical-relative equality, opens one descriptor, rechecks its canonical
identity, and lists or streams through that descriptor. Pull is hard-capped
during host streaming. Its output is staged through locked Windows directory
and file handles, refuses collisions/reparse/hardlink/delete-pending
substitution, and returns the count and SHA-256 from that same stream. Cleanup
deletes only owned handles and never follows a changed path. See
[Optional Rusty Fleet integration](fleet-integration.md).

Core push accepts only an exact staged payload handle with bound size/SHA-256.
It streams that handle to an operation-specific remote partial, validates
remote descriptor identity and content before the final name exists, then
publishes the completed inode with an atomic no-replace hard link. It repeats
descriptor-bound readback and removes the partial name; filesystems without
that atomic primitive fail closed. Durable reservation and
journal documents are exact-identity chained. A share-zero owner handle
distinguishes live work from restart recovery; recovery reports uncertainty
but never retries or performs remote cleanup automatically.

## Observability

Every command returns exit code, standard output, standard error, and elapsed
time. User-facing surfaces show condensed failures without hiding the ADB
message. APK export additionally records local size and SHA-256.
Wi-Fi routes retain the verified endpoint and device row. Parallel routes
retain the deterministic APK path set, concurrency cap, and one command result
or exception summary per target, including partial failures.
The WPF footer shows active status for all operations, five owned Wi-Fi phases,
and completed-target progress for fan-out. It does not invent byte or remaining-
time percentages from ADB prose. The Rusty Kiosk tab additionally shows the
latest PC/headset synchronization receipt rather than optimistic button state.

Future diagnostics bundles will record tool version, command goal, selected
serial placeholder, result class, and artifact types while keeping raw device
evidence local.

## Validation

- Unit tests use a fake process runner and never require a headset.
- Operator-contract tests cover every WPF operation from its exact CLI
  arguments through the serial-scoped ADB projection.
- Parsers cover ready, unauthorized, and offline devices; file paths with
  spaces; package lists; single APKs; and split APK rejection.
- Bundle tests prove one deterministic top-level APK set becomes one
  serial-scoped `install-multiple` invocation.
- Wi-Fi tests prove address inspection precedes transport mutation, explicit
  confirmation is required, and the exact connected endpoint is verified.
- Parallel tests prove the concurrency cap, target de-duplication,
  serial-scoped calls, complete bundle fan-out, and partial-failure retention.
- Progress tests prove explicit indeterminate state, bounded percentage
  derivation, ordered Wi-Fi phases, and exact parallel target completion.
- Mutation tests prove sent/pending/confirmed ordering, wearer-prompt pending
  behavior, later status reconciliation, CPU/GPU property readback, and bounded
  SHA-256 tag transfer without raw Android-data paths.
- Local API tests run without network activation and prove credential bounds,
  explicit loopback admission, strict bounded parsing, closed capabilities,
  retained-command/artifact digests, one-use/replay, expiry, exact read-only
  target preflight, status projection, and operation-local cancellation.
- Fleet interop tests prove disabled/absent/unsupported behavior, strict
  request parsing, exact pre/post serial discovery, bounded list, staged
  pull/hash, remote canonical escape rejection, transfer hard stops,
  final-file/hardlink/parent-junction race defense, timeout/cancellation,
  collision refusal, and handle-owned cleanup.
- Fleet push tests prove unadvertised-without-verifier behavior, exact staged
  input containment, same-stream hashing, authority digest continuity,
  expiry/revocation cancellation, serial pre/post checks, no-overwrite races,
  live/dead owner status, journal substitution rejection, and truthful
  destination/partial uncertainty.
- Fleet connectivity-provider tests prove closed actions, strict request
  admission before initialization, private profile binding, exact USB
  scoping and stable identity continuity for classic TCP/IP, in-process replay
  rejection, sanitized receipts, and independent Kiosk, wearer, and listener
  facts plus the absence of foreign Termux claims.
- Provider-discovery tests prove exact registry equality and stable ordering,
  exhaustive kind/authentication/effect-owner/receipt classification, strict
  DTO shape, deterministic freshness, poisoned factories and stdin, and
  fail-closed alternate argument vectors. Artifact gates repeat the inert
  route with stdin held open and poisoned backend settings.
- CI builds the WPF app, runs the core tests, exercises CLI help, and scans the
  tracked public boundary.
- Live Quest validation is a separate serial-scoped manual gate.

## Reference Lessons

The public Rusty XR Companion proves the usefulness of a shared WPF/CLI core.
Viscereality Companion supplies the long-term Pages, Releases, signing, guided
installer, and verification-harness pattern. The public Meta Quest Agent
Workflow supplies device-operation and evidence boundaries.

The new repo borrows these boundaries and workflow lessons, not app-specific
packages, private behavior, generated binaries, or broad runtime features.

## Mitigation Map

| Risk | Mitigation |
| --- | --- |
| Wrong headset | Require and display the exact ADB serial on every route. |
| Incomplete exported app | Refuse any package with zero or multiple APK paths. |
| Partial bundle install | Snapshot at least two top-level APK paths and pass the complete set to one `install-multiple` operation. |
| Shell injection | Validate serial/package input and quote remote paths with one helper. |
| Hidden device mutation | Require confirmation plus a sent/pending/readback-confirmed receipt; omit delete/uninstall. |
| Optimistic state after a prompt | Keep the receipt pending until later headset status matches. |
| Scoped-storage drift breaks tag files | Transfer fixed provider chunks, verify SHA-256/schema, then atomically hotload. |
| Optional Kiosk breaks file tools | Keep Kiosk detection and commands isolated to its tab/routes. |
| Direct link impersonation or replay | HMAC every request/response, enforce 90-second expiry, persist bounded replay IDs, and permit on-headset code rotation/revocation. |
| Direct mode becomes raw device access | Restrict it to fixed Kiosk routes and one app-owned staging directory; keep general paths in explicit ADB tools. |
| Optimistic local APK success | Require Android PackageInstaller receipt; keep wearer permission/confirmation as pending. |
| Hidden Wi-Fi/daemon mutation | Require approval, scope `tcpip` to one USB serial, scope connect/disconnect to one endpoint, and never reset the ADB server. |
| Kiosk setting is mistaken for usable ADB | Report setting, wearer approval, and listener discovery independently; admit Termux shell proof only in Fleet's signed observation state. |
| Unbounded or ambiguous fan-out | Require two distinct Wi-Fi serials, cap concurrency at 16, and retain every target result. |
| Misleading progress | Use only owned phase/target totals; show every other ADB operation as indeterminate. |
| Misleading backup claim | State clearly that APK export excludes data and assets. |
| Public evidence leak | Ignore artifacts and scan tracked files before publication. |
| Toolchain drift | Discover ADB explicitly and report the selected executable. |
| Fleet chooses the wrong headset | Bind a short-lived observation and rediscover the exact serial and transport before and after work. |
| Fleet requests arbitrary paths | Expose only `adb-shared`, normalized relative paths, and operation-owned local staging. |
| File hook becomes ambient mutation or a scheduler | The CLI remains list/pull/status only; Core push needs injected current authority, stays one target, and has no WPF or fan-out route. |

## Next Slice

Add diagnostics bundles, richer remote metadata, verified folder transfer,
and encrypted direct transport only after a separate authority review. Split APK
export remains a later explicit format with all parts and a manifest installed
as one set.
