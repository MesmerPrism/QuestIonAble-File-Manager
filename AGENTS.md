# Agent Notes

This is a public MIT-licensed repository. Keep every committed file portable
and public-safe.

## Product Boundary

QuestIonAble File Manager is a Windows-first operator tool for ADB-authorized
Meta Quest headsets. It owns file-transfer UX, installed-package inspection,
single-APK export, single-APK and complete split-set installation, diagnostics,
explicit Wi-Fi ADB connection lifecycle, bounded multi-headset installation,
optional Rusty Kiosk installation/operator UX, reviewed Quest power/performance
controls, an optional disabled-by-default single-device Rusty Fleet hook whose
shipped CLI stays read-only and whose Core push requires injected current
Quest/Manifold authority, an optional summary-only encrypted Kiosk v2 catalog
provider whose endpoint and credential remain File Manager-owned, and Windows
delivery. The three dedicated provider artifacts may describe their existing
typed registries through the shared short-lived, target-free, explicitly
non-authorizing discovery contract; description is not backend health,
activation, target resolution, or execution authority. It also owns an
explicit current-user Credential Manager lifecycle for Fleet connectivity
profiles: sanitized status/list, strict private file/stdin or in-memory WPF
enrollment, confirmed replacement, and confirmed revocation. This owner route
does not contact a headset or broaden the dedicated provider. It also owns an
optional distribution-only handoff that verifies one
configured signed Rusty Fleet Windows release and opens Fleet's own guided
installer; this is not Fleet runtime, device, connectivity, or policy
authority. Rusty Kiosk remains a separate AGPL-licensed
Android application and normal file-manager features must work when its APKs
are absent or never installed. This repo does not bypass Android permissions or
promise access to protected app data.

The GUI and CLI must invoke the same typed `OperatorCommand` or
`KioskDirectOperatorCommand` routes. Every headset operation must have an exact
CLI-equivalent route built from the same immutable arguments it executes.
Local-only UI actions such as file selection, list selection, reveal/remask, and
clearing a WPF session must be explicitly classified rather than advertised as
nonexistent CLI routes. Dynamic Kiosk actions must enumerate both their Direct
Link and ADB host-provider route, confirmation, and readback contracts.
`OperatorActionRegistry` and its XAML parity test are the code-owned inventory.
The CLI is an agent and automation surface and is not
displayed in the WPF app. UI handlers collect inputs, invoke shared routes, and
display structured results; they must not hide ADB or filesystem business logic.
Transient WPF progress must come from the optional shared `OperatorProgress`
contract. Use indeterminate state when ADB exposes no honest total; never infer
percentages from console text, elapsed time, or output volume. Keep CLI JSON as
one stable final result rather than mixing progress events into stdout. Every
state-changing route must emit an `OperatorMutationReceipt`: `sent`, then
`pending`, and only `confirmed` after operation-specific headset readback.
Prompt admission or ADB exit code alone is not confirmation. Pending and timed-
out prompt operations remain reconcilable through a later status refresh.

## Public Boundary

Do not commit device serials, private package names, APKs, device captures,
raw logs, signing keys, certificates, local absolute paths, downloaded Android
tools, or generated release artifacts. Use placeholders such as
`<quest-serial>`, `<package>`, `<remote-path>`, and `<path-to.apk>` in docs.

Third-party tools remain under their upstream terms. Do not bundle Android
Platform Tools until its redistribution and update path is reviewed and
documented.

## Device Safety

- Use serial-scoped ADB for every device operation.
- Read-only probes come before mutations.
- Initial file management is limited to list, pull, and explicit push.
- Do not add delete, uninstall, clear-data, or ADB server lifecycle operations
  without a separate safety and UX review.
- The reviewed uninstall exception is only the AgentRoutes-only exact inspected-
  APK cleanup command. It derives the package from immutable artifact bytes,
  requires full single-base installed identity equality and one ready serial,
  warns that app-private data may be deleted, and confirms only fixed package
  absence. A separate workflow must bind an absent pre-run snapshot and
  run-owned install; never retry a pending or cleanup-unknown result.
- The reviewed agent-only property exception accepts only an immutable inspected
  APK, one exact ready serial, and a closed
  `rusty.quest.android_property_manifest.v1` file whose owner package equals the
  APK. `observe` writes one create-new snapshot. Confirmed `clear` and `restore`
  consume that exact snapshot and manifest; callers cannot supply property names
  or values. Clear rejects a stale snapshot before dispatch. Both mutations
  rediscover the exact ready serial immediately before their fixed `setprop`
  loop. `sent` begins only at the first fixed dispatch; `pending` begins only
  once an effect may exist and exact readback is awaited or unavailable. They
  confirm only exact manifest readback while the same APK bytes remain installed.
  They are AgentRoutes-only: no WPF, Local API, arbitrary shell,
  generic property, retry, or overwrite surface is admitted.
- The reviewed agent-only `apk launch-diagnose` exception accepts only one
  immutable inspected standalone APK with no installed splits, one exact ready
  serial, and one new private
  output directory. It derives package, launcher, a non-shared current-user UID,
  and UID-bound package/process PIDs;
  arms one fixed UID-filtered logcat process at a device-time fence before one
  resolved launch; then stops and drains that process tree. It exposes no
  caller package, UID, PID, tag, duration, command, shell, or raw-ADB surface,
  and it does not appear in WPF or the local API. Capture or readback ambiguity
  remains `outcome_unknown`; never retry the launch automatically.
- Wi-Fi ADB enable/connect/disconnect is the reviewed exception documented in
  `docs/wifi-adb-and-parallel-install.md`. Every route requires explicit
  operator confirmation. Enablement reads `wlan0` before mutation, scopes
  `tcpip` to one ready USB serial, connects one validated endpoint, and
  requires one stable identity to match before USB mutation and after exact
  network-endpoint connection. Never reset or restart the ADB server as part
  of this workflow.
- Parallel installation requires at least two distinct ready Wi-Fi ADB
  serials, uses a bounded 1–16 concurrency limit, sends one serial-scoped
  install transaction per headset, and preserves per-target partial failures.
- APK export supports one installed APK path and rejects split packages rather
  than producing an incomplete backup.
- APK bundle install accepts one folder containing at least two top-level APKs
  and passes the complete deterministic set to one serial-scoped
  `adb install-multiple` call. It does not recurse or install independent apps
  one by one.
- A copied APK does not include app data, OBB files, downloaded assets, or
  store entitlement.
- Reviewed device controls are limited to explicit keep-awake/restore and fixed
  CPU/GPU level/clear operations. They require confirmation and effective-state
  readback; they are not generic shell access.
- Rusty Kiosk host control is restricted to its exported `DUMP`-protected,
  versioned provider. It admits fixed typed commands and bounded SHA-256 tag
  chunks. Never add arbitrary intents, components, shell commands, or host-
  supplied headset paths to that contract.
- Authorized-USB Direct Link bootstrap is restricted to one exact ready
  classic-USB serial and one fixed Stable/Labs product contract. Provider
  credentials must use the bounded sensitive byte runner and remain absent
  from arguments, environment, command results, progress, help, errors, and
  telemetry. Manual direct credentials are accepted only through masked WPF
  memory or bounded CLI standard input. Clear them on every connection outcome,
  explicit disconnect, focus/deactivation timeout, window close, and process
  exit; never add a clipboard route. A CLI direct command is one atomic session
  and must reconcile cleanup before its one final JSON result. Disable only a
  listener that the exact operation/session/generation owns, then poll provider
  status until both enabled and running are false on the post-disable
  generation. Preserve pre-existing listeners and report non-convergence as
  `cleanup_unknown`. If the sensitive enable response is lost or malformed,
  recover only through `direct-recover-disable` with the original operation ID,
  then accept only no-argument stopped-state readback; never reconstruct or
  request a credential. A Direct Link install must submit the exact name,
  positive byte count, and lowercase SHA-256 returned by each upload so Kiosk
  can verify the same opened handle while copying into PackageInstaller. An
  incomplete `cleanup-required` receipt may replay only the identical install
  body with a fresh authenticated transport ID; it must never create a second
  logical install or be projected as terminal failure before abandonment or
  session-absence readback. Preserve exact ordered commitment/digest binding;
  damaged private receipt or replay-ledger state fails closed and must never be
  interpreted as absent or freshly initialized.
- Rusty Fleet integration v1 is disabled by default. Its environment-created
  CLI adapter is restricted to strict JSON capability discovery, exact-serial
  observation, bounded `adb-shared` list/pull, and read-only durable status.
  Core push must remain unadvertised without an injected current Quest identity
  plus Manifold command/lease/revocation verifier. It is one target, staged
  input only, size/SHA bound, no-overwrite, descriptor-validated, and one-use.
  Recheck the identical verified authority digest before the stream and after
  exact-serial readback; stop before the earlier request/authority expiry.
  Durable recovery distinguishes destination/partial uncertainty and never
  retries or deletes remotely by itself. No delete, overwrite, WPF,
  multi-target, or ADB daemon route is admitted. Never replace bounded streams
  with `adb pull`/`adb push`, post-transfer-only checks, recursive cleanup, or
  path-only reparse checks.
- The separate Fleet/Kiosk v2 catalog subprocess is summary-only and reads one
  strict request from standard input. It resolves an opaque File Manager profile
  from the current user's secure store; Fleet never supplies or receives its
  endpoint, pairing code, keys, session plaintext, launch scope, or Manifold
  barrier. File Manager independently verifies the fresh public Kiosk contract,
  session proof, directional HKDF/AES-GCM exchange, nonce/counter/AAD bindings,
  and owner snapshot. Only the exact encrypted envelopes, deliberately
  exportable owner catalog, and owner grant receipt cross back to Fleet as
  bounded base64url evidence. Do not add v1 fallback, endpoint arguments,
  inherited-environment credentials, launch, mutation, or free-form errors.
- The separate Fleet awake-control subprocess owns only closed exact-serial
  status, bounded hold, drift-only repair, temporary device-watchdog, stop,
  and restore actions. Keep the hold bound at `60000..28800000` milliseconds
  and watchdog polling at `1000..60000` milliseconds. A Windows watchdog is
  Fleet-owned and calls `repairOnce`; the device watchdog is a fixed File
  Manager-owned `/data/local/tmp` helper with generation, boot, process,
  heartbeat, and repair-counter readback. It is not reboot-persistent.
  Same-generation reuse requires the exact requested interval.
  `stopWatchdogs` must leave power/proximity settings unchanged;
  `restoreNormal` must prove the helper inactive and recheck request expiry
  after the stop wait before restoring them. Receipts report
  stay-on, proximity hold, wake/display, watchdog, and restore facts
  independently and must not expose serials, controller identifiers, raw ADB
  output, caller shell, paths, or process commands.
- The separate Fleet connectivity subprocess owns only the closed `status`,
  `request_wireless_adb`, after-boot preference enable/disable,
  `disable_wireless_adb`, and classic USB `tcpip 5555` actions. It resolves
  endpoint, Kiosk pairing code, and exact USB serial from a File Manager-owned
  current-user credential profile keyed by Fleet device ID; Fleet never
  supplies or receives those values. Kiosk remains the privileged effect
  owner and Meta prompts remain wearer decisions. Keep request delivery,
  Kiosk setting, after-boot preference, wearer approval, and listener
  discovery independent. A Kiosk setting never proves listener usability.
  Termux proof belongs to Fleet's separate signed observation state and must
  not appear in this File Manager-owned receipt. Reject duplicate request IDs
  and operation IDs within the provider process before effect dispatch.
  Current-user Credential Manager is not same-user process isolation and the
  request carries no cryptographic Manifold proof; do not claim otherwise.
  Deploy under an isolated Windows identity when same-user callers are not
  trusted.
- Fleet may launch awake control only through the self-contained, single-file
  `questionable-file-manager-awake-provider.exe`, pinned by lowercase SHA-256
  and staged with a per-launch private `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Its
  execution vector is exact case-sensitive
  `integration quest-awake --json`. Its separate inert discovery vector is
  exactly `--describe-json`; that route must return before stdin, provider
  factory, ADB, target, or state use. Invalid, mixed, case-varied, and extra
  arguments and strict-request failures reject before ADB discovery. Never
  substitute the general CLI or a framework-dependent apphost.
- Fleet may launch Wi-Fi ADB connectivity only through the self-contained,
  single-file `questionable-file-manager-connectivity-provider.exe`, pinned by
  lowercase SHA-256 and staged with a per-launch private
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR`. Its execution vector is exact
  case-sensitive `integration quest-connectivity --json`. Its separate inert
  discovery vector is exactly `--describe-json`; that route must return before
  stdin, profile, provider factory, Kiosk, ADB, target, or replay-state use.
  Invalid, mixed, case-varied, and extra arguments and strict-request failures
  reject before profile or ADB initialization. Never substitute the general
  CLI or a framework-dependent apphost.
- The optional Fleet installer handoff is distribution bootstrap only. It
  accepts no caller URL, program, argument, credential, device, ADB, hotspot,
  or elevation choice. Published releases embed one all-or-none versioned trust
  configuration: the exact canonical
  `https://mesmerprism.com/Rusty-Fleet/metadata/<channel>/release.json`
  descriptor, descriptor RSA SPKI and pin, channel, Setup signer pin, and safe
  per-user relative state root. Embedded configuration is authoritative and
  ignores environment overrides. Environment and explicitly enabled local
  fixture configuration are development-only fallbacks. MesmerPrism Pages is
  metadata-only: never publish, derive, or download a sibling Setup binary
  there. The strict v4 signed payload binds the exact immutable channel-specific
  `https://github.com/MesmerPrism/rusty-fleet/releases/download/v<version>[-<maturity>.<sequence>]/RustyFleet[-Labs]-Setup.exe`
  asset URL, distribution axes, timestamp requirement, and Authenticode trust
  posture. Release trust exists only as the complete eight-field metadata
  block in checked-in `FleetInstallerReleaseConfiguration.cs`, reviewed on
  the exact clean tagged release commit. Ordinary MSBuild, environment,
  release-script arguments, and generated `obj` files have no trust authority.
  Core verifies RFC 8785 JCS payload bytes, schema,
  product, exact three-part version/tag, the
  required duration/exact expiry relation, 24-hour maximum lifetime,
  30-second future-skew boundary, fresh-clock post-guided-success check, asset
  name/size/SHA-256/protocol, RSA-PSS signature,
  Authenticode, and the Fleet-owned `--plan --json` result before starting the
  exact installer with no arguments. Keep staging local, non-reparse, retained against
  substitution, create-new, and cleaned by handle. Persist replay and
  strictly-monotonic-version state with write-through after guided success,
  plus a sibling file anchor and an elevated signed-Setup-provisioned,
  root-bound HKLM record with SYSTEM/Administrators write and Users read. The
  machine record owns accepted descriptor IDs and the monotonic version
  high-water mark; per-user files are not authoritative for those decisions.
  Setup must verify its own Authenticode and reviewed signer pin before write;
  Core must expose no direct machine-state writer. Its only transition route is
  the protected signed Setup copy under Program Files, which elevates,
  re-fetches, and independently verifies the current signed descriptor before
  advancing the record. Serialize descriptor refetch, protected record
  read/check/write, and exact durable readback under a machine-wide mutex with
  a protected SYSTEM/Administrators-only DACL. Revalidate that DACL on every
  open, use the same lock for provisioning/repair, and re-read state after
  abandoned-lock recovery. Elevated Setup staging must be unpredictable,
  protected under Program Files, reparse-free, per-run, and cleaned; quiet
  success/failure output must never contain its path.
  Missing/coordinated-deleted replay files or marker loss must fail closed;
  signed Setup repair may reconstruct local files only from the protected
  machine record and must preserve its high-water mark, accepted IDs, and root
  binding. Mutable local evidence must never reconstruct a missing machine
  record. Keep destructive reset behind the separate explicit
  `--destructive-reset-fleet-replay-protection` route, mutually exclusive with
  repair. Replace the Program Files helper atomically only when the retained
  old and new artifacts share the reviewed signer pin; preserve replay state,
  and report rollback only after the prior backup identity/hash/signer and the
  restored destination receive exact retained readback. Retain the backup when
  rollback is unknown or fails; describe it as validated repair evidence only
  after exact prior-commitment validation. Treat a thrown atomic-replace call
  as potentially state-changing and reconcile destination, replacement, and
  backup against retained commitments without deleting prior evidence. Apply
  the same rule if rollback's atomic restore throws: reconcile the destination,
  rollback candidate, and original backup, preserve unresolved evidence, and
  never report restored without exact stable destination readback. Reject signer
  changes without a separately reviewed rotation route.
  A declined, failed, or prompt-expired visible guided run remains unconsumed;
  recheck the clock immediately after guided success and require a fresh
  descriptor fetch before retry.
  Status and handoff receipts must never contain
  source URLs, local paths, process arguments, credentials, or device data.
  File Manager must not download another bootstrapper, configure Fleet,
  perform hidden elevation, or claim that Fleet installation succeeded merely
  because the handoff began. See `docs/fleet-installer-handoff.md`.
- Fleet may launch the Kiosk v2 catalog provider only from the reviewed,
  self-contained, single-file Windows artifact named
  `questionable-file-manager-kiosk-v2-provider.exe`, pinned by lowercase
  SHA-256. Publish it directly from
  `QuestIonAbleFileManager.FleetKioskV2Provider`; never rename or package the
  general operator CLI as the provider. Never point Fleet at a `dotnet build`
  apphost; it loads mutable sibling assemblies and is not a complete trust
  unit. The dedicated entrypoint admits the exact case-sensitive execution
  vector `integration kiosk-v2-catalog --json` and the separate exact inert
  discovery vector `--describe-json`. Discovery must return before stdin,
  credential/profile, provider, HTTP, target, or owner-session use. The
  entrypoint cannot dispatch ADB, file, APK, Wi-Fi, Kiosk, kiosk-direct, or
  device-control commands.
  The artifact gate must smoke-run a stage containing only the dedicated EXE
  and an empty private `bundle-extract` subdirectory, require the exact
  absent-profile response and exit code, and require zero standard-error bytes.
  It must also prove broad verbs reject before provider initialization and an
  ordinary framework-dependent apphost fails when isolated. Fleet supplies a
  new private `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for each pinned provider stage so
  any native single-file extraction remains inside that stage's trust boundary;
  it never reuses a shared extraction directory.
- Meta permission prompts remain wearer decisions. A Kiosk
  `wifi_adb_enabled` readback proves only that setting; it does not prove
  wearer acceptance, a current listener, or Termux loopback shell authority.

## Agent CLI Workflow

Use the CLI for all automated or agent-driven operation checks. Never scrape,
click, or expose a command transcript from the WPF window. During source work,
the prefix is:

```powershell
dotnet run --project src/QuestIonAbleFileManager.Cli --
```

The optional `QuestIonAbleFileManager.Api` executable is a separate,
Windows-only, inert-until-started loopback projection of only the
inspected-deployment typed
registry. It requires `QUESTIONABLE_FILE_MANAGER_API_BEARER`, refuses
non-loopback listeners, and is not a general CLI/ADB wrapper. Private staged
state and its integrity secret must be explicitly configured through
`QUESTIONABLE_FILE_MANAGER_API_STATE` and
`QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET`; its local private root, retained
non-reparse handles, journal/anchor chain, capacity reservations, and
journal-before-delete cleanup ordering are part of the reviewed boundary. See
`docs/local-api.md`. Do not start it during ordinary build/test validation.

In a published Windows archive, invoke `questionable-file-manager.exe` directly.
The former `meta-quest-file-manager.exe` name is a deprecated release-only
compatibility alias; new documentation, tests, and automation use the canonical
executable.
Start with read-only discovery, select one ready serial explicitly, and then
run the narrow operation:

```powershell
questionable-file-manager.exe devices --json
questionable-file-manager.exe files list --serial <quest-serial> --path /sdcard --json
questionable-file-manager.exe files pull --serial <quest-serial> --remote <remote-path> --output <local-path>
questionable-file-manager.exe files push --serial <quest-serial> --file <local-path> --remote <remote-path>
questionable-file-manager.exe apk list --serial <quest-serial> --json
questionable-file-manager.exe apk inspect --file <path-to.apk> --json
questionable-file-manager.exe apk export --serial <quest-serial> --package <package> --output <local-apk>
questionable-file-manager.exe apk install --serial <quest-serial> --file <local-apk>
questionable-file-manager.exe apk launch --serial <quest-serial> --file <path-to.apk> --json
questionable-file-manager.exe apk launch-diagnose --serial <quest-serial> --file <path-to.apk> --output <new-private-folder> --json
questionable-file-manager.exe apk observe --serial <quest-serial> --file <path-to.apk> --json
questionable-file-manager.exe apk properties observe --serial <quest-serial> --file <path-to.apk> --manifest <property-manifest.json> --output <new-snapshot.json> --json
questionable-file-manager.exe apk properties clear --serial <quest-serial> --file <path-to.apk> --manifest <property-manifest.json> --snapshot <snapshot.json> --confirm-exact-apk-property-mutation --json
questionable-file-manager.exe apk properties restore --serial <quest-serial> --file <path-to.apk> --manifest <property-manifest.json> --snapshot <snapshot.json> --confirm-exact-apk-property-mutation --json
questionable-file-manager.exe apk install-bundle --serial <quest-serial> --folder <apk-folder>
questionable-file-manager.exe wifi enable --serial <usb-serial> --port 5555 --confirm-wifi-adb
questionable-file-manager.exe wifi connect --host <quest-ip> --port 5555 --confirm-wifi-adb
questionable-file-manager.exe wifi disconnect --host <quest-ip> --port 5555 --confirm-wifi-adb
questionable-file-manager.exe apk install-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --file <local-apk> --parallelism 2 --json
questionable-file-manager.exe apk install-bundle-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --folder <apk-folder> --parallelism 2 --json
questionable-file-manager.exe kiosk status --serial <quest-serial> --json
questionable-file-manager.exe kiosk install --serial <usb-serial> --product-channel stable --confirm-kiosk-setup
questionable-file-manager.exe kiosk command --serial <quest-serial> --command request-wifi-adb --confirm-kiosk-control --json
questionable-file-manager.exe kiosk tags import --serial <quest-serial> --file <tag-file> --confirm-kiosk-control --json
questionable-file-manager.exe kiosk-direct status --serial <usb-serial> --product-channel <stable|labs> --confirm-kiosk-direct-bootstrap --json
questionable-file-manager.exe kiosk-direct command --serial <usb-serial> --product-channel <stable|labs> --confirm-kiosk-direct-bootstrap --command launch-kiosk --confirm-kiosk-control --json
questionable-file-manager.exe kiosk-direct request-status --serial <usb-serial> --product-channel <stable|labs> --confirm-kiosk-direct-bootstrap --request-id <request-id> --json
questionable-file-manager.exe operator-actions --json
questionable-file-manager.exe device status --serial <quest-serial> --json
questionable-file-manager.exe device keep-awake --serial <quest-serial> --on --confirm-device-settings --json
questionable-file-manager.exe device performance --serial <quest-serial> --cpu 3 --gpu 3 --confirm-device-settings --json
questionable-file-manager.exe integration capabilities --json
questionable-file-manager.exe integration observe --serial <quest-serial> --json
questionable-file-manager.exe integration invoke --request <operation-request.v1.json> --json
questionable-file-manager.exe integration status --operation <operation-id> --json
questionable-file-manager.exe fleet status --json
questionable-file-manager.exe fleet install --confirm-fleet-install --json
questionable-file-manager.exe connectivity-profile status --device-id <fleet-device-id> --json
questionable-file-manager.exe connectivity-profile list --json
questionable-file-manager.exe connectivity-profile import --file <private-profile.json> --confirm-profile-write --json
questionable-file-manager.exe connectivity-profile import --stdin --confirm-profile-write --json
questionable-file-manager.exe connectivity-profile revoke --device-id <fleet-device-id> --confirm-profile-revoke --json
```

The WPF buttons map to those routes exactly. Both install actions accept
`--no-replace`, `--downgrade`, `--grant-runtime-permissions`, and `--test-only`.
`install-bundle` snapshots every top-level `.apk` path in the folder, orders
the base APK first when recognizable, and installs all parts atomically as one
package set. ADB rejects mixed package names, versions, signatures, or missing
required splits. Pass `--adb <path>` to select a particular ADB executable
without changing global ADB state.

The single-APK `install`, `deploy`, `launch`, `observe`, and `diagnose` routes inspect the local
artifact with Android SDK Build Tools and bind package/version/signer plus
base-APK digest/size readback to the exact selected serial. See
`docs/inspected-deployment.md`. Use `apk deploy` for the bounded agent fast path;
repository-specific build and semantic diagnostic instructions remain owned by
the source repository as described in `docs/agent-quest-apk-workflow.md`.
`apk diagnose` is read-only on the headset and writes only a new, no-overwrite
private local evidence directory through the fixed capture set documented in
`docs/apk-diagnostic-bundle.md`.

The `--confirm-wifi-adb` flag records that an operator approved the exact
Wi-Fi state change; agents must not add it without that approval. Parallel
install commands exit nonzero when any target fails, but their JSON result
still contains every headset outcome. See
`docs/wifi-adb-and-parallel-install.md` for the full authority and evidence
contract.

## Build And Validation

Use PowerShell 7.6 or newer through `pwsh` for maintained scripts.

```powershell
dotnet build QuestIonAbleFileManager.slnx --configuration Release
dotnet test QuestIonAbleFileManager.slnx --configuration Release
dotnet run --project src/QuestIonAbleFileManager.Cli -- --help
dotnet test tests/QuestIonAbleFileManager.Core.Tests --configuration Release --filter "FullyQualifiedName~LocalApiTests"
pwsh -NoProfile -File ./tools/Test-PublicBoundary.ps1
pwsh -NoProfile -File ./tools/Test-BrandingContract.ps1
pwsh -NoProfile -File ./tools/Test-FleetInstallerReleaseConfiguration.ps1
pwsh -NoProfile -File ./tools/Test-FleetInstallerHandoffLifecycle.ps1 -InputPath <private-lifecycle-input.json> -QfmSetupExecutablePath <exact-qfm-setup.exe>
pwsh -NoProfile -File ./tools/app/Test-BrandAssets.ps1
pwsh -NoProfile -File ./tools/Test-FleetKioskV2ProviderArtifact.ps1
pwsh -NoProfile -File ./tools/Test-FleetAwakeProviderArtifact.ps1
pwsh -NoProfile -File ./tools/Test-FleetConnectivityProviderArtifact.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File ./tools/Test-ProviderCapabilityDiscovery.ps1 -ContractRoot <meta-quest-agent-workflow-root>
```

The canonical folder mark and multi-resolution Windows icon live under
`assets/branding`. Run `tools/app/New-BrandAssets.ps1` after changing the mark;
it regenerates the EXE icon, Windows package logos, favicon, browser icons, and
site copy from the same geometry. Do not hand-edit one generated surface alone.

For signed release work, first use `--plan` to validate the exact guided
installer inputs without changing Windows trust or package state:

```powershell
artifacts/release/QuestIonAbleFileManager-Setup.exe --plan --json `
  --certificate-source artifacts/release/QuestIonAbleFileManager.cer `
  --appinstaller-source artifacts/release/QuestIonAbleFileManager.appinstaller
```

Build and verify all public assets through the shared release route:

```powershell
pwsh -NoProfile -File ./tools/app/Invoke-ReleaseBuild.ps1 `
  -Version <version> `
  -ExpectedKioskVersion <kiosk-version> `
  -ExpectedKioskSourceRevision <kiosk-source-commit> `
  -PackageCertificatePath ./artifacts/signing/windows-signing.pfx `
  -PackageCertificatePassword <pfx-password> `
  -FleetInstallerLifecycleInputPath <private-lifecycle-input.json>

pwsh -NoProfile -File ./tools/app/Test-ConsumerInstall.ps1 `
  -ReleaseDirectory ./artifacts/release `
  -RemoveAfterTest
```

If that release enables the optional Fleet distribution handoff, add the
complete public eight-field metadata block to the checked-in release
configuration source as documented in `docs/release-workflow.md`. Leave the
source inert otherwise. Never rely on ambient MSBuild/environment/script
values or generated files, and never commit private publisher material. The
private lifecycle input names exact externally staged Fleet A/B release
directories and independently reviewed public pins; it is never a release
asset or committed source file.

Use `-DirectPackage` only when the agent shell cannot accept UAC. That fallback
validates the helper plan and signed MSIX install/launch separately, and the
receipt sets `guided_install_validated` to false. Do not report it as a guided
installer pass.

Never print, log, commit, or persist the PFX password in a script. The guided
window and `--quiet` route both invoke the same `GuidedInstaller`; actual
installation requests UAC because Local Machine certificate trust and the App
Installer update association are part of the release contract.

Run the app:

```powershell
dotnet run --project src/QuestIonAbleFileManager.App
```

## Architecture

- `QuestIonAbleFileManager.Core` owns process execution, ADB discovery, command
  construction, typed operator commands, output parsing, transfers, APK
  install/export, Wi-Fi endpoint lifecycle, bounded fan-out, progress units,
  hashes, typed Kiosk hosting, and mutation reconciliation.
- `QuestIonAbleFileManager.Cli` is the automation-equivalent operator surface.
- Core's optional Fleet installer handoff is a distribution consumer, not a
  Fleet provider or device-control route. The WPF “Get Fleet” tab and exact
  `fleet` CLI routes share its typed `OperatorCommand`.
- `QuestIonAbleFileManager.FleetAwakeProvider` is the narrow Fleet effect-owner
  artifact for Quest awake control; it is not a general CLI projection.
- `QuestIonAbleFileManager.FleetConnectivityProvider` is the narrow Fleet
  execution artifact for Kiosk-owned wireless requests and exact-USB classic
  TCP/IP setup; it is not a general CLI projection.
- `ProviderCapabilityDiscoveryProjection` is the shared Core-only inert
  description of the awake, connectivity, and Kiosk catalog registries. It
  owns no execution, target, profile, credential, backend, or effect truth.
- Fleet integration stays in the Core/CLI boundary described in
  `docs/fleet-integration.md`; do not add a WPF projection or broaden it beyond
  one read-only target without a separate authority and UX review.
- `QuestIonAbleFileManager.App` is the Windows WPF projection.
- Keep external processes behind `ICommandRunner` and preserve cancellation
  and bounded timeouts.
- Use `ProcessStartInfo.ArgumentList`; never construct a host shell command.
- Keep future Android and Apple clients as adapters over explicit contracts,
  not as reasons to put platform UI into the core.
- A GUI/CLI parity test must cover every WPF operation before a new button is
  accepted.

## Release Posture

GitHub Pages is the human-facing download surface and GitHub Releases is the
binary source of truth. The workflow publishes the signed guided setup, signed
MSIX, App Installer feed, public CER, portable app/CLI archives, three dedicated
Fleet provider executables and receipts, checksums, and a validation receipt.
The build verifies the exact published Kiosk version and
tag commit, every manifest byte count and SHA-256, both APK signer digests, and
the source pointer before packaging; the receipt retains that provenance.
Published assets are never overwritten—any change requires a new version.
The signed package identity `MesmerPrism.MetaQuestFileManager` remains stable
for update continuity. Rebranded releases publish canonical asset names plus
byte-identical former-name aliases for the documented migration window; do not
remove those aliases without a separately reviewed update-compatibility release.
`product_channel` is persistent `stable|labs`, defaults to Stable, and requires
explicit opt-in for Labs. `maturity` is independently
`alpha|beta|rc|released`; canonical `vX.Y.Z-alpha.N` tags denote maturity only
and map to numeric `X.Y.Z.N` Windows versions. `distribution_track` is a third,
independently bounded `github-release|github-prerelease` axis. Stable uses
`github-release`; Labs uses `github-prerelease` and exact-tag URLs, the
`MesmerPrism.QuestIonAbleFileManager.Labs` identity, and
`QuestIonAbleFileManager-Labs-*` assets and staging names. It consumes only an
exact published Rusty Kiosk bundle-v2 release whose product channel, maturity,
distribution track, tag, revision, URL, signer, manifest hash, coinstallable
core/helper package identities, Kiosk owner metadata, and closed asset set match protected
release policy. Labs Setup may provision and invoke Fleet replay authority
only when the complete checked-in Fleet block declares channel `labs`, its
state root is exactly `QuestIonAbleFileManagerLabs/FleetInstaller`, and the
Setup's own signer hash matches the reviewed provisioning pin. The root digest
isolates its HKLM record from Stable; the protected helper binds every
accept/repair/reset request to that exact embedded root. Reject absent,
partial, cross-channel, stable-root, ambient, or wrong-signer configuration.
Labs releases also publish the QFM-owned
`questionable-file-manager-labs-owner-release.json` v2 catalog asset. Keep it
deterministic and Labs-only, binding all three axes, exact tag/versions, source
commit and tree, Labs package identity, and primary Setup name/hash/bytes. Supporting
workflow receipts are evidence, not release authority.

Private signing material is supplied only through the
Windows certificate store, ignored `artifacts`, and GitHub Actions secrets.
Never commit private certificate material or generated release assets.
