# QuestIonAble File Manager

QuestIonAble File Manager is a Windows-first desktop application for browsing
the ADB-accessible storage on a Meta Quest headset, copying files in either
direction, installing user-supplied APKs, and exporting sideloaded single-APK
apps back to a Windows file.

The repository also ships a CLI over the same core operations so every GUI
action can be automated and tested.

> This is an independent open-source project. It is not affiliated with or
> endorsed by Meta. Meta Quest is a trademark of Meta Platforms, Inc.

## Current First Slice

- discover USB or already-connected ADB devices;
- browse absolute device paths, starting at `/sdcard`;
- pull a selected file to Windows;
- push an explicitly selected Windows file to the current device folder;
- list third-party Android packages;
- export an installed app when Android reports exactly one APK path;
- write a SHA-256 sidecar for every exported APK;
- reject split APK packages with an actionable explanation;
- install a user-supplied APK with explicit reinstall, downgrade, runtime
  permission, and test-package options;
- install every top-level APK in a selected bundle folder together through one
  atomic split-package installation;
- enable Wi-Fi ADB from a selected USB-authorized headset, connect or disconnect
  an enabled endpoint, and distinguish USB from Wi-Fi device rows;
- install one APK or one complete split-package folder on multiple checked
  Wi-Fi ADB headsets with bounded parallelism and per-headset results;
- use the PC's authorized ADB connection as the default APK installation path,
  avoiding an in-headset confirmation for each package during unattended or
  batch installs;
- show honest operation progress: indeterminate when ADB provides no total,
  phase-based for Wi-Fi setup, and target-based for parallel installs;
- optionally install and provision the separately licensed Rusty Kiosk app and
  same-signer setup helper, without making file-manager features depend on them;
- search Kiosk apps, filter/edit tags, preserve named entries for apps not
  installed on this headset, hotload tag files, and launch normally or guarded;
- request/disable Wi-Fi ADB, manage its after-restart prompt preference, and
  enable/disable Kiosk Accessibility through fixed typed routes;
- after one-time setup, connect directly to the wearer-enabled Rusty Kiosk
  link for the same search/tag/launch/guard controls without routine ADB;
- list, upload, download, and delete files in Rusty Kiosk's bounded app-owned
  staging area, then optionally submit one base or base-and-split APK set to
  Android's wearer-confirmed PackageInstaller when ADB is unavailable;
- show headset/controller batteries, apply Meta's bounded keep-awake hold for
  one minute through eight hours or restore normal power, and set or clear
  fixed Quest CPU/GPU levels;
- track every PC mutation as sent, pending, then headset-confirmed (or failed/
  timed out) instead of treating process success as effective state;
- optionally expose a disabled-by-default Rusty Fleet hook for one exact-device
  `/sdcard` listing or staged pull, plus Core-only no-overwrite push when a host
  injects current Quest-identity and Manifold mutation-authority verification;
- optionally expose a second, summary-only Fleet catalog provider that performs
  Rusty Kiosk's encrypted direct-operator v2 exchange using a File Manager-owned
  Windows credential profile, while remaining unavailable when no profile was
  explicitly enrolled;
- optionally expose a dedicated hash-pinned Fleet awake provider for bounded
  Meta holds, drift-only Windows watchdog repairs, a temporary device watchdog,
  watchdog-only stop, and explicit normal-settings restore;
- optionally expose a dedicated hash-pinned Fleet connectivity provider that
  resolves File Manager-owned private profiles, invokes only Kiosk's fixed
  Wi-Fi ADB setup actions, or performs separate exact-USB classic TCP/IP setup;
- let each of those three dedicated provider artifacts return a short-lived,
  target-free, explicitly non-authorizing description of its existing typed
  registry without reading stdin or initializing a backend;
- optionally verify one configured signed Rusty Fleet Windows release and open
  Fleet's own guided installer, without giving File Manager Fleet device,
  connectivity, hotspot, credential, policy, or hidden-elevation authority;
- expose the same typed routes through a Windows WPF app and CLI;
- keep the automation-oriented CLI out of the non-technical WPF interface;
- publish a signed MSIX, App Installer update feed, guided setup helper, and
  portable fallback archives.

The app does not copy app data, saves, OBB folders, downloaded asset packs, or
store entitlements. ADB only exposes paths permitted to the Android shell
user; this is not unrestricted access to the entire headset filesystem.

## Requirements

- Windows 10 version 2004 or later;
- .NET 10 SDK for source builds;
- Android SDK Platform Tools (`adb`) for bootstrap, general shared-path tools,
  package export, advanced installs, device settings, and diagnostics;
- a Meta Quest with Developer Mode enabled and this computer authorized for
  USB debugging.
- for Wi-Fi ADB, the PC and headset must share a reachable network; a USB
  connection is required once to enable the headset listener.
- for Rusty Kiosk direct mode, an installed Kiosk 0.6.0+ on the same trusted
  network; direct mode itself does not require ADB.

ADB is located in this order:

1. `QUESTIONABLE_FILE_MANAGER_ADB`;
2. deprecated compatibility alias `META_QUEST_FILE_MANAGER_ADB`;
3. `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`;
4. `%ANDROID_SDK_ROOT%` or `%ANDROID_HOME%`;
5. `adb` on `PATH`.

## Build

```powershell
dotnet build QuestIonAbleFileManager.slnx
dotnet test QuestIonAbleFileManager.slnx
dotnet run --project src/QuestIonAbleFileManager.App
pwsh -NoProfile -ExecutionPolicy Bypass -File ./tools/Test-ProviderCapabilityDiscovery.ps1 -ContractRoot <meta-quest-agent-workflow-root>
```

## Install

The [project download page](https://mesmerprism.com/QuestIonAble-File-Manager/)
offers the guided Windows setup, manual signed-package route, and portable
fallback, along with a first-use walkthrough for Platform Tools, Quest Developer
Mode, USB authorization, file transfer, APK work, and Wi-Fi ADB. The guided
helper requests administrator approval to trust the public package certificate
and register the App Installer update feed. See the [release workflow](docs/release-workflow.md)
for signature and Smart App Control limitations.

Version `0.4.0` is the first release under the QuestIonAble File Manager name.
It retains the signed Windows package identity and former release-asset aliases
so existing `0.3.x` installations and pinned automation can update without
reinstallation. See [Branding and compatibility](docs/branding-and-compatibility.md).

## CLI

These `dotnet run` commands are source-development examples. Production Fleet
does not use this invocation or a build-output apphost for its Kiosk catalog
provider.

```powershell
dotnet run --project src/QuestIonAbleFileManager.Cli -- devices
dotnet run --project src/QuestIonAbleFileManager.Cli -- files list --serial <quest-serial> --path /sdcard
dotnet run --project src/QuestIonAbleFileManager.Cli -- files pull --serial <quest-serial> --remote /sdcard/Download/example.txt --output ./example.txt
dotnet run --project src/QuestIonAbleFileManager.Cli -- files push --serial <quest-serial> --file ./example.txt --remote /sdcard/Download/example.txt
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk list --serial <quest-serial>
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk inspect --file ./example.apk --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk export --serial <quest-serial> --package com.example.app --output ./com.example.app.apk
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk install --serial <quest-serial> --file ./example.apk
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk launch --serial <quest-serial> --file ./example.apk --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk observe --serial <quest-serial> --file ./example.apk --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk install-bundle --serial <quest-serial> --folder ./example-apk-set
dotnet run --project src/QuestIonAbleFileManager.Cli -- wifi enable --serial <usb-serial> --port 5555 --confirm-wifi-adb
dotnet run --project src/QuestIonAbleFileManager.Cli -- wifi connect --host <quest-ip> --port 5555 --confirm-wifi-adb
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk install-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --file ./example.apk --parallelism 2 --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk install-bundle-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --folder ./example-apk-set --parallelism 2 --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk status --serial <quest-serial> --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk install --serial <usb-serial> --confirm-kiosk-setup --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk tags export --serial <quest-serial> --output ./app-tags.v1.json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk tags import --serial <quest-serial> --file ./app-tags.v1.json --confirm-kiosk-control --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk-direct status --endpoint http://<quest-ip>:39873 --pairing-code <on-headset-code> --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk-direct command --endpoint http://<quest-ip>:39873 --pairing-code <code> --command launch-kiosk --confirm-kiosk-control --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk-direct files upload --endpoint http://<quest-ip>:39873 --pairing-code <code> --file ./example.apk
dotnet run --project src/QuestIonAbleFileManager.Cli -- kiosk-direct install --endpoint http://<quest-ip>:39873 --pairing-code <code> --file ./example.apk --confirm-local-install --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- device status --serial <quest-serial> --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- device keep-awake --serial <quest-serial> --on --confirm-device-settings --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- device performance --serial <quest-serial> --cpu 3 --gpu 3 --confirm-device-settings --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- integration capabilities --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- integration observe --serial <quest-serial> --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- integration invoke --request <operation-request.v1.json> --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- integration status --operation <operation-id> --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- fleet status --json
dotnet run --project src/QuestIonAbleFileManager.Cli -- fleet install --confirm-fleet-install --json
dotnet run --project src/QuestIonAbleFileManager.FleetKioskV2Provider -- integration kiosk-v2-catalog --json < <strict-request.json>
dotnet run --project src/QuestIonAbleFileManager.FleetAwakeProvider -- integration quest-awake --json < <strict-request.json>
dotnet run --project src/QuestIonAbleFileManager.FleetConnectivityProvider -- integration quest-connectivity --json < <strict-request.json>
dotnet run --project src/QuestIonAbleFileManager.FleetKioskV2Provider -- --describe-json
dotnet run --project src/QuestIonAbleFileManager.FleetAwakeProvider -- --describe-json
dotnet run --project src/QuestIonAbleFileManager.FleetConnectivityProvider -- --describe-json
```

The optional `questionable-file-manager-api` executable is inert unless
explicitly started. It requires a private bearer value in
`QUESTIONABLE_FILE_MANAGER_API_BEARER`, a private state directory in
`QUESTIONABLE_FILE_MANAGER_API_STATE`, and a private journal integrity secret
in `QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET`. It accepts only an explicit
numeric loopback listener:

```powershell
dotnet run --project src/QuestIonAbleFileManager.Api -- --listen http://127.0.0.1:8123/
```

It projects only the bounded inspected-deployment registry and is not a generic
CLI, shell, or ADB wrapper. Private request fields are intentionally omitted
from public examples. See [Dedicated local API](docs/local-api.md).

Pass `--json` to list commands for machine-readable output. Pass `--adb` to
select an explicit ADB executable without changing global machine settings.
The Windows release archive places `QuestIonAbleFileManager.exe` and
`questionable-file-manager.exe` beside each other. It temporarily also carries
`meta-quest-file-manager.exe` as a deprecated compatibility alias. The CLI is intended for agents,
automation, and advanced operator workflows; it is not displayed in the GUI.
Wi-Fi state changes require an explicit confirmation in the WPF app or the
`--confirm-wifi-adb` CLI flag. The app never resets the global ADB server.
Kiosk setup/control and device settings use their own confirmation flags.
Mutation JSON contains desired and observed state plus its transition history.
A Meta permission prompt can legitimately remain pending until wearer response.
The optional [Fleet installer handoff](docs/fleet-installer-handoff.md) is a
distribution bootstrap, available in the WPF **Get Fleet** tab and the exact
`fleet status` / `fleet install` CLI routes even when ADB is absent. It accepts
no runtime URL or executable argument. Core verifies the configured signed
release descriptor, exact `RustyFleet-Setup.exe` size and SHA-256, Windows
signer, and Fleet's non-mutating plan before opening the visible Fleet-owned
installer. Receipts expose release evidence but no source URL, local path,
credential, process argument, or device data. Fleet remains the authority for
all fleet/device/connectivity behavior.
The optional [Rusty Fleet integration](docs/fleet-integration.md) is disabled
until the operator configures an approved staging root. Its shipped v1 CLI
routes are single-device and read-only: bounded list, staged pull, and durable
status. The Core can advertise bounded no-overwrite push only through an
injected current-authority verifier; environment settings and caller-supplied
IDs cannot enable it. Push locks a staged input handle, binds size/SHA-256,
preserves exact serial and authority digest across rechecks, and uses remote
no-clobber partial staging followed by atomic no-replace publication of the
verified inode. Unsupported remote filesystems fail closed. Durable status distinguishes live
ownership from recovery and reports final/partial uncertainty without
automatic retry or cleanup. No route exposes delete, overwrite, multi-target
fan-out, ADB daemon lifecycle, or WPF automation.
The separate `kiosk-v2-catalog` route does not use those ADB settings. It reads
one strict summary request from standard input and looks up only its opaque
`profile_id` in the current Windows user's Credential Manager. The shipped
default is `unavailable`; there is no CLI option or environment variable for
an endpoint, pairing code, or key. Ordinary File Manager and Kiosk-direct
features do not depend on this optional provider.
Fleet uses only the release's dedicated
`questionable-file-manager-kiosk-v2-provider.exe`. It pins that executable's
lowercase SHA-256 and stages only that file into its private launch directory.
The executable is published directly from the narrow
`QuestIonAbleFileManager.FleetKioskV2Provider` project; it is never a renamed
copy of the general operator CLI. Its execution vector is the exact
case-sensitive `integration kiosk-v2-catalog --json`; its other admitted
vector is the separate exact inert `--describe-json` route. A framework-
dependent `bin/Release` apphost is not an acceptable provider artifact because
it can load mutable sibling assemblies. The release gate proves the dedicated
self-contained single-file executable rejects the general CLI's file, APK,
Wi-Fi, Kiosk, kiosk-direct, device, and integration verbs before provider
initialization, then returns the
strict absent-profile response from a stage containing only the EXE and its
empty private `bundle-extract` subdirectory without
writing to standard error, while the ordinary apphost fails without its
siblings. Fleet assigns a new private `DOTNET_BUNDLE_EXTRACT_BASE_DIR` to each
pinned provider stage for any native bundle extraction.
The provider's strict status/exit mapping is `verified=0`, `failed=1`,
`rejected=2`, and `unavailable=3`; Fleet checks both values and requires empty
standard error.

All three dedicated provider executables implement that same exact
`--describe-json` route. It returns the shared
`rusty.quest.workflow.provider_capability_discovery.v1` shape before stdin,
provider factories, profiles, ADB, targets, backends, or state are used.
Actions are projected from the awake and connectivity registries and Kiosk's
single catalog-summary scope. The five-minute descriptor contains no target,
path, executable location, endpoint, credential, invocation, or authority.
`descriptor-available` describes only the local typed surface; it does not
prove a usable backend, approval, activation, or owner-effective result.
Mixed, case-varied, or extra description arguments fail closed.
The separate Fleet awake provider is documented in
[Quest awake control](docs/quest-awake-control.md). It owns only the
exact-serial ADB effects and independent readbacks for bounded holds and
watchdogs. Fleet owns targets, confirmation, Manifold authorization,
scheduling, and the Windows loop. The published trust unit is
`questionable-file-manager-awake-provider.exe`, not the general CLI or a
framework-dependent build apphost. Canonical releases place that executable
and its validation receipt beside the Kiosk provider at the top level and in
both portable archives.
The separate [Quest connectivity provider](docs/quest-connectivity-provider.md)
resolves endpoint, Kiosk pairing material, and exact USB serial from a
File Manager-owned current-user credential profile. Fleet supplies only its
bounded device binding and current authority. Kiosk remains the on-device
effect owner; the dedicated provider never accepts Meta system UI. Its receipt
keeps request delivery, Kiosk setting, wearer approval, listener discovery,
and effect readback independent, and never returns the private profile fields.
Termux usability belongs only to Fleet's separate signed observation state.
The classic `tcpip 5555` action remains separate from modern TLS Wireless
Debugging and confirms that the network endpoint reports the same stable
device identity as the selected USB transport. The provider rejects request
and operation replay within one process. Its Credential Manager profile is
current-user protection, not isolation from another process under that user;
the current request has no cryptographic Manifold caller proof. Use a separate
Windows identity when same-user callers are outside the trust boundary.
Direct mode uses expiring HMAC-signed requests, replay IDs, body hashes, and
signed responses. Its v1 HTTP bodies are not encrypted, so use a trusted local
network or a private Windows hotspot. The pairing code can be supplied through
`RUSTY_KIOSK_PAIRING_CODE` instead of a command-line argument.

The **APKs (ADB default)** tab is the normal installation route. Once the PC's
ADB key is authorized, it can install multiple packages without repeated
in-headset confirmation. Kiosk's direct local installer is an attended fallback:
the one-time “install unknown apps” grant allows Kiosk to request installs, but
Android can still require one confirmation for every app installation session.
A base APK and its split APKs are submitted together as one session.

## Design And Safety

- [Architecture](docs/architecture.md)
- [Quest awake control](docs/quest-awake-control.md)
- [Quest connectivity provider](docs/quest-connectivity-provider.md)
- [ADB scope and safety](docs/adb-scope-and-safety.md)
- [GUI and CLI operator parity](docs/operator-cli-parity.md)
- [Wi-Fi ADB and parallel installation](docs/wifi-adb-and-parallel-install.md)
- [Two-headset Wi-Fi validation receipt](docs/wifi-adb-parallel-live-validation-2026-07-17.md)
- [Progress reporting contract](docs/progress-reporting.md)
- [Rusty Kiosk integration and synchronization](docs/rusty-kiosk-integration.md)
- [Optional Rusty Fleet integration](docs/fleet-integration.md)
- [Optional Fleet installer handoff](docs/fleet-installer-handoff.md)
- [Inspected single-device deployment](docs/inspected-deployment.md)
- [Dedicated local API](docs/local-api.md)
- [Release workflow](docs/release-workflow.md)
- [Branding and compatibility](docs/branding-and-compatibility.md)
- [Reference intake](docs/reference-intake.md)

## Roadmap

1. Add a Quest-owned Fleet-device identity proof before any unattended Fleet
   file mutation.
2. Add split-APK set export with a manifest and stronger package-set validation.
3. Add transport encryption after a separate protocol-version and Horizon
   compatibility review.
4. Add diagnostics bundles and no-device UI verification.
5. Define portable contracts for future Android and Apple host clients.

## License

MIT. See [LICENSE](LICENSE). Android Platform Tools and other optional external
tools retain their own licenses and are not included in this source tree.
Official Windows binaries may aggregate the separate Rusty Kiosk APK bundle,
licensed AGPL-3.0-or-later with its license, source link, and hashes included.
