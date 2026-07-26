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
Quest/Manifold authority, and Windows delivery. Rusty Kiosk remains a separate AGPL-licensed
Android application and normal file-manager features must work when its APKs
are absent or never installed. This repo does not bypass Android permissions or
promise access to protected app data.

The GUI and CLI must invoke the same typed `OperatorCommand` routes. Every GUI
operation must have an exact CLI-equivalent route built from the same immutable
arguments it executes. The CLI is an agent and automation surface and is not
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
- Wi-Fi ADB enable/connect/disconnect is the reviewed exception documented in
  `docs/wifi-adb-and-parallel-install.md`. Every route requires explicit
  operator confirmation. Enablement reads `wlan0` before mutation, scopes
  `tcpip` to one ready USB serial, and connects one validated endpoint. Never
  reset or restart the ADB server as part of this workflow.
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
- Meta permission prompts remain wearer decisions. Showing a Wi-Fi ADB prompt
  is `pending`; it becomes `confirmed` only when Kiosk reports Wi-Fi ADB enabled.

## Agent CLI Workflow

Use the CLI for all automated or agent-driven operation checks. Never scrape,
click, or expose a command transcript from the WPF window. During source work,
the prefix is:

```powershell
dotnet run --project src/QuestIonAbleFileManager.Cli --
```

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
questionable-file-manager.exe apk export --serial <quest-serial> --package <package> --output <local-apk>
questionable-file-manager.exe apk install --serial <quest-serial> --file <local-apk>
questionable-file-manager.exe apk install-bundle --serial <quest-serial> --folder <apk-folder>
questionable-file-manager.exe wifi enable --serial <usb-serial> --port 5555 --confirm-wifi-adb
questionable-file-manager.exe wifi connect --host <quest-ip> --port 5555 --confirm-wifi-adb
questionable-file-manager.exe wifi disconnect --host <quest-ip> --port 5555 --confirm-wifi-adb
questionable-file-manager.exe apk install-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --file <local-apk> --parallelism 2 --json
questionable-file-manager.exe apk install-bundle-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --folder <apk-folder> --parallelism 2 --json
questionable-file-manager.exe kiosk status --serial <quest-serial> --json
questionable-file-manager.exe kiosk install --serial <usb-serial> --confirm-kiosk-setup
questionable-file-manager.exe kiosk command --serial <quest-serial> --command request-wifi-adb --confirm-kiosk-control --json
questionable-file-manager.exe kiosk tags import --serial <quest-serial> --file <tag-file> --confirm-kiosk-control --json
questionable-file-manager.exe device status --serial <quest-serial> --json
questionable-file-manager.exe device keep-awake --serial <quest-serial> --on --confirm-device-settings --json
questionable-file-manager.exe device performance --serial <quest-serial> --cpu 3 --gpu 3 --confirm-device-settings --json
questionable-file-manager.exe integration capabilities --json
questionable-file-manager.exe integration observe --serial <quest-serial> --json
questionable-file-manager.exe integration invoke --request <operation-request.v1.json> --json
questionable-file-manager.exe integration status --operation <operation-id> --json
```

The WPF buttons map to those routes exactly. Both install actions accept
`--no-replace`, `--downgrade`, `--grant-runtime-permissions`, and `--test-only`.
`install-bundle` snapshots every top-level `.apk` path in the folder, orders
the base APK first when recognizable, and installs all parts atomically as one
package set. ADB rejects mixed package names, versions, signatures, or missing
required splits. Pass `--adb <path>` to select a particular ADB executable
without changing global ADB state.

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
pwsh -NoProfile -File ./tools/Test-PublicBoundary.ps1
pwsh -NoProfile -File ./tools/Test-BrandingContract.ps1
pwsh -NoProfile -File ./tools/app/Test-BrandAssets.ps1
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
  -PackageCertificatePassword <pfx-password>

pwsh -NoProfile -File ./tools/app/Test-ConsumerInstall.ps1 `
  -ReleaseDirectory ./artifacts/release `
  -RemoveAfterTest
```

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
MSIX, App Installer feed, public CER, portable app/CLI archives, checksums, and
a validation receipt. The build verifies the exact published Kiosk version and
tag commit, every manifest byte count and SHA-256, both APK signer digests, and
the source pointer before packaging; the receipt retains that provenance.
Published assets are never overwritten—any change requires a new version.
The signed package identity `MesmerPrism.MetaQuestFileManager` remains stable
for update continuity. Rebranded releases publish canonical asset names plus
byte-identical former-name aliases for the documented migration window; do not
remove those aliases without a separately reviewed update-compatibility release.
Private signing material is supplied only through the
Windows certificate store, ignored `artifacts`, and GitHub Actions secrets.
Never commit private certificate material or generated release assets.
