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
- show headset/controller batteries, keep awake or restore normal power, and
  set or clear fixed Quest CPU/GPU levels;
- track every PC mutation as sent, pending, then headset-confirmed (or failed/
  timed out) instead of treating process success as effective state;
- optionally expose a disabled-by-default Rusty Fleet hook for one exact-device
  `/sdcard` listing or staged pull, plus Core-only no-overwrite push when a host
  injects current Quest-identity and Manifold mutation-authority verification;
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

```powershell
dotnet run --project src/QuestIonAbleFileManager.Cli -- devices
dotnet run --project src/QuestIonAbleFileManager.Cli -- files list --serial <quest-serial> --path /sdcard
dotnet run --project src/QuestIonAbleFileManager.Cli -- files pull --serial <quest-serial> --remote /sdcard/Download/example.txt --output ./example.txt
dotnet run --project src/QuestIonAbleFileManager.Cli -- files push --serial <quest-serial> --file ./example.txt --remote /sdcard/Download/example.txt
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk list --serial <quest-serial>
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk export --serial <quest-serial> --package com.example.app --output ./com.example.app.apk
dotnet run --project src/QuestIonAbleFileManager.Cli -- apk install --serial <quest-serial> --file ./example.apk
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
```

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
- [ADB scope and safety](docs/adb-scope-and-safety.md)
- [GUI and CLI operator parity](docs/operator-cli-parity.md)
- [Wi-Fi ADB and parallel installation](docs/wifi-adb-and-parallel-install.md)
- [Two-headset Wi-Fi validation receipt](docs/wifi-adb-parallel-live-validation-2026-07-17.md)
- [Progress reporting contract](docs/progress-reporting.md)
- [Rusty Kiosk integration and synchronization](docs/rusty-kiosk-integration.md)
- [Optional Rusty Fleet integration](docs/fleet-integration.md)
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
