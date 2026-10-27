# Browser/PWA feature and platform migration

The intended human interface is a complete PWA over QFM's shared Core. The
current browser is an incremental implementation. A CLI alternative below
preserves a useful operation during migration; it does not count as completed
browser parity. The code-owned `OperatorActionRegistry`, immutable
`OperatorCommands`, `KioskDirectOperatorCommand` and exhaustive
`RustyKioskCommand` remain the bounded action inventories.

## Architecture and platform status

The same-origin PWA talks to a separately started authenticated loopback
connector. It never exposes arbitrary shell or local filesystem paths. Agents
retain the typed CLI and existing local API. App-owned streaming readiness
stays with the app. Readback, pending operations, protected prompts and cleanup
requirements stay with shared Core.

| Host / transport | Implementation and proof | Supported alternative |
| --- | --- | --- |
| Windows connector | Ordinary ADB Core routes; native profile/installer services available. Browser unit/DOM fixtures and inert host checks. | Existing typed Windows CLI/WPF. |
| Linux connector | Portable .NET host; actual startup/auth/static/session and Core discovery tested with a fake ADB executable. No hardware acceptance claimed. | Host Platform Tools and typed connector routes; use Windows host for protected profiles/Fleet installer. |
| macOS connector | Portable host composition and tool discovery implemented; runtime/hardware execution not yet verified. | Existing authorized ADB tools; use a verified host until this platform is tested. |
| Browser USB | Existing explicit, read-only WebUSB serial/boot/battery proof, browser/driver dependent. | Keep shared ADB running and use the local connector when USB is busy/unavailable. |
| Phone / tablet | Responsive shell/help; browser installation varies. Localhost cannot reach another computer's connector. | Use the PWA on the attached computer; no remote connector mode is claimed. |
| Offline | Installed static shell/help, honest disconnected state; no API/private cache or queued effects. | Start the local connector and explicitly connect/read current state. USB can work while the wider network is offline. |
| Protected staging | Windows NTFS upload/download/cleanup and shared Core APK inspection. Native handles, ACLs and exact byte identity verified. | Linux/macOS report unavailable; use Windows companion for these staged-path features pending a portable equivalent. |

## Complete operator migration map

| Existing human/agent action family | Shared command owner / current browser | Host capability and remaining migration |
| --- | --- | --- |
| Discover USB / connected Wi-Fi, select target, refresh | `devices`; browser implemented. | Host ADB and ready exact target; no page-load discovery. |
| Classic Wi-Fi enable/connect/disconnect | `wifi enable/connect/disconnect`; browser implemented. | Host transport effect, endpoint readback and exact confirmation. |
| Directory list/open/up | `files list`; browser implemented. | Serial-scoped shell-readable absolute paths and literal file-name rendering. |
| Pull/download and push/upload | Generic upload and explicitly approved ordinary Core push; bounded pull to opaque browser download implemented. | Immutable staged source and full remote filename; explicit overwrite approval, observed destination type and remote size readback only. Pull independently checks captured bytes; verified save remains separate. Unknown writes never replay. |
| Package list / single APK export + SHA256 | `apk list`; bounded single-APK export with opaque receipt/checksum implemented. | Export is only installed APK, no app data/OBBs/assets/entitlements. Splits reject before streaming; package paths rechecked afterwards. |
| APK inspect/preflight | Staged inspect and actual SDK/installed-byte/launcher readiness preflight implemented; retained operation preparation is separate. | Windows protected staging and host Build Tools. Readiness reads need no Native API state/key; effect preparation retains its stable configured owner. |
| Install single / complete split set / parallel targets | Retained inspected single-APK install implemented; bundle/parallel projections pending. | Existing retained v1 replace/grant options and exact installed-byte readback; no downgrade/test-only expansion or queued offline install. Complete atomic split/parallel workflows remain with CLI. |
| Deploy / launch / observe / diagnose / launch-diagnose | Reviewed retained exact single-target deploy, resolved launch, observation and private read-only diagnostic capture/download implemented; transient exact and explicit development-metadata runtime reads implemented. Launch-diagnose remains pending. | Deploy and diagnostics have explicit versioned digest/operation domains in the same durable owner. Diagnostics bind exact installed bytes and publish a bounded private ZIP only after durable acknowledgement. Lost originals never replay. Capture completeness and transport/process/focus evidence do not establish app readiness. |
| Stop / exact uninstall / permissions / exact-property snapshot-clear-restore | Existing closed APK factories; browser permission observation implemented, other projections pending. | Preserve package/byte ownership, explicit destructive cleanup/property confirmation, immutable snapshots and no replay after unknown effects. No generic property/shell UI. |
| Status / power / bounded keep-awake / restore normal | `device status/keep-awake`; browser implemented. | Fresh readback; proximity duration does not restore stay-on. No supervisor/watchdog custody transfer. |
| CPU/GPU performance set/clear | `device performance`; browser implemented. | Fixed reviewed levels and matching device-settings confirmation/readback. |
| Actor / actor identity / shared forward inventory | `device actor-observe/actor-identity-observe`, `adb forwards`; browser implemented. | Read-only facts, exact serial/PID; forward inventory never owns or mutates shared daemon mappings. |
| Kiosk install/provision/catalog/status | Bounded passive installation and explicit catalog-open implemented; install/provision pending. | Exact Stable/Labs channel and fixed products; catalog open can request Kiosk activity. Reported installation/control flags are not fresh installed-signer verification. Kiosk stays optional. |
| Kiosk search/filter/tag/select/launch and opaque options | Local returned-catalog filtering and explicitly reviewed ADB selection implemented. Direct normal/kiosk launch of the current selection implemented; named-app launch, opaque options and tag edits pending. | Select binds a completed host-session catalog and unique eligible key/package. Direct launch is a separate operation addressing the selection at execution time. Its guard-state acknowledgement does not prove named-app launch or readiness. |
| Kiosk tag-file export/import | Direct signed tag backup to a protected transient download implemented; import remains pending. | Exact original v1/v2 document bytes, capped at 256 KiB; shared opaque connection/gate and verified Save. No tag write/reload or current-state/readiness claim. Other hosts/providers retain the typed CLI alternative. |
| Kiosk launch requirements/cancel, setup-helper, reload, Wi-Fi request/disable/boot policy, accessibility, passthrough and Meta Home | Direct status, controls/apps panels, provider reload, setup-helper check, launch-requirement preference and pending-launch cancellation implemented. Remaining `RustyKioskCommand` projections pending. | Fixed owner routes and wearer decisions; preserve pending requests and supported readback. Reload acknowledgement does not prove a fresh catalog or foreground. A launch preference does not change Wi-Fi or approve a wearer prompt. Pending-launch cancellation does not acknowledge an older logical request. |
| Direct Link authentication/USB bootstrap/adoption/status/request status/cancel/disconnect | Explicit USB bootstrap/adoption, signed status, opaque-handle close and logical request status/result/cancel implemented; manual authentication pending. | Transient host-only credential, exact USB/channel/session binding, finite key expiry and separate owned-stop/pre-existing-preservation cleanup. Invoke saves its logical identity before dispatch. Lifecycle status alone cannot acknowledge it; original typed result and readback are required. Lost originals never replay; no credential reconstruction or saved-ID restart cleanup. |
| Direct staging list/upload/download/delete and attended single/split install | Signed inventory and explicitly approved artifact-bound upload implemented; download/delete and attended install remain with `kiosk-direct files/install/install-status`. | Inventory is historical metadata. Upload binds protected source bytes to the provider's signed exact name/size/SHA-256 acknowledgement and may replace a same-name file; unknown writes never replay. It proves neither APK validity nor installation. Existing Core Install performs its own uploads; this receipt is not a new install prerequisite. |
| Fleet release status / guided installer handoff | Explicit Windows `fleet.status` read implemented; guided install remains CLI/WPF. | Host-owned signed metadata/replay facts only, with no ADB/profile prerequisite or installed-health claim. No installer route or trust override. Other OSs use the Windows owner alternative. |
| Connectivity profile list/status/import/create/replace/revoke and USB-to-Direct enrollment | Local Windows profile card implements list/status, manual/private-file stdin enrollment, explicit replace and revoke through the shared manager. | Current-user Credential Manager only; no plaintext fallback or connectivity test. Private input uses a separate bounded route, exact reviewed ID, shared gate and sanitized receipts. Unknown writes never reconcile from status; transient USB Direct credentials are not exported/enrolled. |
| Optional Fleet provider capabilities/list/pull / authority-injected push/cancel; dedicated Kiosk/awake/connectivity providers | Existing separate machine contracts remain machine interfaces. | Surface capability and invocation guidance without widening provider authority, hidden activation or transferring managed watchdog custody. |
| File/folder pickers, select-all/clear, tabs, settings, copy/export views | Local WPF interactions classified by registry; browser replacements pending. | Browser pickers/downloads/preferences must respect private-file and platform boundaries. |
| Installation, signed updates, portable fallback, frontend retirement | Existing Windows MSIX/App Installer/setup/archive; PWA shell and optional framework-dependent development connector archives implemented. | Extracted Windows/Linux startup/auth/CSP/offline verified; six closed RID publish targets, no macOS runtime claim. Signed connector installation/updates and full browser parity/distribution verification remain; WPF retirement follows those checks. |

## Validation and next slices

The initial shell uses backend security tests, DOM fixtures and a real Chromium
worker/offline test. Platform smoke tests use invented targets and fake runners,
and establish no device effects. See `tools/browser-ui/README.md` and
`browser-operator.md` for callable checks.

Continue with the remaining APK workflows, retaining unknown-effect artifact
custody. Protected local staging/inspection, file transfer, retained single-APK
install/deploy/launch, runtime reads and private diagnostic capture/download are
implemented. Diagnostic output remains in the authenticated owner; browser Save
uses a separately verified transient copy and never captures again. Portable
native-path staging, complete split/parallel workflows, properties and
launch-diagnose remain outstanding. Remaining Kiosk, Fleet, profile enrollment
and distribution work follow the same action inventory.
Update each row at tested checkpoints, with device evidence separate from
source, browser and packaging proof. Do not block the existing product developer
loop on completion of this UI migration.

Current protected capacity is 1 GiB per artifact, eight files and 2 GiB total.
Streaming Save verifies incrementally before file close where supported;
buffered fallback remains bounded to 256 MiB and cannot assert final disk-save
completion. Actual SDK compatibility, installed-byte classification and launcher
readiness are now a separate read projection, distinct from retained-operation
preparation and application readiness.
