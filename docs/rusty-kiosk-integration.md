# Rusty Kiosk Integration And Synchronization

Rusty Kiosk is an optional, separately licensed Android application bundled by
official Windows releases for convenient installation. QuestIonAble File Manager
continues to browse files, transfer APKs, and manage ordinary ADB connections
when the bundle is absent or Kiosk is never installed.

## First-Use Flow

1. Enable Quest Developer Mode through Meta's supported account/device flow.
2. Connect the headset over USB-C and approve this computer's ADB key in-headset.
3. Open the Rusty Kiosk tab and choose **Install and provision (USB)**.
4. The file manager installs the same-signer setup helper, grants only that
   helper `WRITE_SECURE_SETTINGS`, installs Kiosk, and reads back both package
   and permission states.
5. In the Windows Kiosk tab, select the installed `stable` or `labs` Kiosk
   channel and choose **Connect using authorized USB**. File Manager verifies
   the exact classic-USB serial, installed package/UID, and channel-bound host
   v3 provider before accepting one ephemeral session. Manual fallback uses the
   address and masked credential shown in the headset panel. Routine Kiosk
   commands, tags, app-owned staging, and optional attended APK installs then
   use the authenticated local link. The PC's ADB installer remains the default
   APK route.
6. Wi-Fi ADB remains optional. If requested, Meta shows its own permission
   prompt and the PC receipt remains pending until wearer acceptance/readback.
7. Optionally enable **Ask after restart**. After a reboot, Kiosk can request
   Meta's Wi-Fi ADB allowance again; it cannot accept that allowance for the
   wearer. USB-C remains the recovery path if no ADB transport is reachable.
8. Enable Accessibility only when guarded launches are wanted. It is a separate
   explicit choice and can be disabled again from either surface.

## Desktop Functions

The tab displays the complete Kiosk catalog, including tag-file entries named
for apps not installed on the current headset. Search matches app name, package,
or tag. Tag filtering, tag add/remove, normal launch, and guarded launch use the
same Kiosk command semantics as the headset panel.

Tag files use `rusty.kiosk.app_tags.v1`. Entries may identify an app by name
without a package. Import/export uses provider chunks rather than direct access
to `/sdcard/Android/data`: each chunk is bounded, the complete file is capped at
256 KiB, SHA-256 is checked, the schema is parsed, and activation is atomic.

Direct mode also exposes one app-owned staging area. Windows can list, upload,
download, and delete its bounded filenames. An APK install names one to 32
staged `.apk` parts and creates one Android PackageInstaller session. Android's
visible per-app installer permission and confirm/cancel surface remain wearer
owned; a request is pending until its matching receipt becomes installed or
failed. Trusting Kiosk as an install source is a one-time grant, but arbitrary
first-time app installs can still require one confirmation per package session;
base and split APKs for one app share that session. Therefore the **APKs (ADB
default)** tab is the normal unattended and batch-install path. General
shell-visible headset paths, package export, advanced install flags, CPU/GPU
settings, and diagnostics remain optional ADB functions.

## Authority Boundary

The successor release host surface is schema `rusty.kiosk.host_operator.v3`,
protected by caller-held `android.permission.DUMP`. Stable uses
`content://io.github.mesmerprism.rustykiosk.operator`; Labs uses
`content://io.github.mesmerprism.rustykiosk.labs.operator`. File Manager chooses
one fixed product contract rather than accepting a package or authority from
the device. The host can admit only the fixed Kiosk command enum, query/cancel
one exact request lifecycle, transfer the fixed tag document, and issue/revoke
a bounded direct session. It cannot supply shell text, Android components,
intent actions, setup endpoints, or headset paths.

Kiosk retains ownership of launch and watchdog behavior. The setup helper owns
the small secure-settings operations. The Windows app owns ADB transport and
operator confirmation.

Authorized-USB bootstrap uses provider schema
`rusty.kiosk.direct_usb_bootstrap.v1`. The sensitive provider response is read
through a bounded byte-only runner and is never projected into ordinary command
results, arguments, environment, help, telemetry, or progress. The session
secret remains in process memory and is zeroed when the client closes. The
provider's immediate enable/disable result is only admission: File Manager
retries authenticated HTTP status during startup, and for an owned listener it
polls no-argument provider status until both `direct_enabled` and
`direct_running` are false on the exact post-disable generation. A generation
change or non-convergence produces `cleanup_unknown`; it is never reported as
confirmed. A listener that was already enabled is never disabled by the PC.

The optional direct surface is `rusty.kiosk.direct_operator.v1` on port 39873.
It accepts expiring HMAC-SHA-256 envelopes, retains replay IDs, verifies request
bodies, and signs every authenticated response. The Windows client additionally
requires the completed result's logical request ID and typed command to match
the exact invocation; stale, crossed, wrong-command, and incomplete results
cannot be returned as accepted. It has no raw shell, arbitrary intent/component,
protected-data path, or device-settings endpoint. HTTP v1 is authenticated and
integrity-protected but not encrypted; use a trusted network or private Windows
hotspot. This is a single-headset local link, not fleet management.

Direct mutations are admitted separately from completion. The PC can query
`/v1/kiosk/request-status` or cancel exactly one still-pending request through
`/v1/kiosk/cancel`; crossed request IDs and unknown lifecycle states fail
closed. Attended installer receipts retain the distinct
`pending_wearer_action` state. CLI authorized-USB sessions finish cleanup before
the one final JSON document is emitted, including a sanitized cleanup receipt.

## Sent, Pending, Confirmed

Every PC-originated mutation has an operation ID and transition history:

- `sent` records what was requested and where it was sent;
- `pending` means effective-state evidence has not matched yet;
- `confirmed` requires route-specific headset readback;
- `failed` records an explicit error;
- `timed_out` means no match was seen within five minutes, but later refreshes
  may still reconcile a wearer prompt.
- `pending_wearer_action`, `rejected`, `expired`, and `cancelled` preserve the
  provider's typed request lifecycle without collapsing it into success;
- `cleanup_unknown` means an owned session could not prove listener shutdown.

Examples of confirmation evidence include Kiosk's guard/accessibility/Wi-Fi/tag
state, same-signer permission readback, remote file size, refreshed package
inventory, the exact connected ADB endpoint, Quest power state, and Oculus
CPU/GPU properties. A displayed Meta permission prompt is never itself treated
as enabled state.

## Distribution

The Windows repository is MIT-licensed. Rusty Kiosk is a separate
AGPL-3.0-or-later work. Official Windows packages aggregate the owner-issued
bundle-v2 APK set with the Kiosk license, source URL/revision, and SHA-256
manifest. QFM validates exact `product_channel`, `maturity`, and bounded
`distribution_track` axes (`github-release` for Stable and
`github-prerelease` for Labs). Labs requires pinned, mutually distinct,
separate-coinstallable core and setup-helper identities plus exact Kiosk owner
metadata. The separate Meta Store launcher package
`io.github.mesmerprism.rustykiosk.launcher.labs` is not a GitHub bundle asset
and QFM does not hash or signer-bind it. Stable validation remains on the
Stable axes. The release build rejects debug bundles and any
axis, identity, version, source, byte-count, hash, source-pointer, or signer
mismatch. Kiosk retains package, signer, updater, permission, installation,
and receipt authority.
