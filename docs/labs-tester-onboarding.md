# Labs tester onboarding

QuestIonAble File Manager Labs is the guided Windows setup route for the
co-installable Rusty Kiosk Labs preview. The exact immutable pair documented
here is File Manager `v0.5.0-alpha.14` and Kiosk `v0.6.6-alpha.9`.

## Exact public links

- [Guided File Manager Labs setup](https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/v0.5.0-alpha.14/QuestIonAbleFileManager-Labs-Setup.exe)
- [Complete File Manager Labs release](https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/tag/v0.5.0-alpha.14)
- [Rusty Kiosk Labs tester guide](https://mesmerprism.com/Rusty-Kiosk/#labs)
- [Meta Alpha invite](https://www.meta.com/s/4SlXf1lVo)
- [Exact Kiosk Labs release](https://github.com/MesmerPrism/Rusty-Kiosk/releases/tag/v0.6.6-alpha.9)
- [Exact Fleet Labs release](https://github.com/MesmerPrism/rusty-fleet/releases/tag/v0.1.0-alpha.8)
- [Fleet Labs guided setup](https://github.com/MesmerPrism/rusty-fleet/releases/download/v0.1.0-alpha.8/RustyFleet-Labs-Setup.exe)

Labs prereleases are immutable exact-version releases and are deliberately not
the repository's `latest` stable download.

## First setup

1. Install current
   [Android SDK Platform Tools](https://developer.android.com/tools/releases/platform-tools)
   plus Android SDK Build Tools (`aapt2` and `apksigner`). Alpha.14 verifies
   both immutable staged Kiosk APKs with those Build Tools before its first
   headset install command.
2. Run the guided Labs setup. It explains the project certificate before
   asking Windows to trust it and register the signed Labs app.
3. Enable Developer Mode on the Quest, connect it by USB, put on the headset,
   and accept the USB debugging prompt.
4. In File Manager Labs, select the ready `[USB]` Quest and open
   **Rusty Kiosk**.
5. Confirm the **Labs** channel and select
   **Install and provision (USB)**. The installer embeds the exact signed
   Kiosk Alpha.9 core and same-signer setup helper. Alpha.14 binds install,
   provisioning, permission readback, and the typed provider to that selected
   Labs identity without falling back to Stable.
6. Follow the [Kiosk Labs tester guide](https://mesmerprism.com/Rusty-Kiosk/#labs)
   for on-headset setup, the Meta Alpha launcher, and the test checklist.
7. For the optional advanced Fleet preview, open **Get Fleet**, refresh the
   signed Labs release status, and choose the confirmed guided-install route.
   File Manager verifies Fleet's short-lived signed descriptor, exact
   alpha.8 Setup bytes, signer, and non-mutating plan before opening Fleet's
   own installer. Fleet enrollment and Manifold-backed device authority remain
   separate, explicit configuration.

The Kiosk core and Meta launcher are deliberately separate. The Meta launcher
can open a trusted installed Kiosk core, but cannot install, update, provision,
or manage it.

## Alpha.12 setup issue and unpublished Alpha.13 attempt

Do not use File Manager Labs `v0.5.0-alpha.12` to install or provision Kiosk
Labs. That build bundled the correct Labs APKs but its two USB setup buttons
incorrectly checked and granted the Stable helper identity, producing the
message that the helper grant, same-signer control, or typed host operator did
not verify. Other channel-aware Kiosk status, command, tag, and playlist routes
were unaffected. The supported workaround is to update File Manager Labs to
Alpha.14 and repeat **Install and provision (USB)** with **Labs** selected;
Android retains the already-installed Labs app data across the replacement
install. Do not switch to Stable as a fallback for a Labs bundle.

Alpha.13 failed closed during its release gate and was never published. Android
Build Tools 37 changed `apksigner --print-certs` from the numbered signer format
used by Build Tools 34 through 36 to a scheme-prefixed current-signer format.
The gate rejected the unfamiliar output before dispatching an APK install.
Alpha.14 accepts the exact numbered and scheme-prefixed single-signer output
families, including V3 effective-range lines, while continuing to reject
absent, past-lineage, source-stamp, multiple distinct current signers, and V3.2
hybrid signer sets that the single-signer identity contract cannot represent.

Alpha.14 also makes the CLI channel choice explicit: existing automation must
add `--product-channel stable` or `--product-channel labs` to `kiosk install`
and `kiosk provision`. Omitting, duplicating, or misspelling that option now
rejects before any headset command instead of assuming Stable. The Core API
keeps Stable as its backward-compatible default for callers that construct a
typed command directly.

## Optional Connection Hub preview

The standalone Rusty Connection Hub is an optional direct-APK preview. It is
not currently presented as a guided installer or as an entry in the central
distribution catalog.

- [Exact Connection Hub prerelease](https://github.com/MesmerPrism/rusty-quest/releases/tag/connection-hub-v0.1.0-alpha.3)
- [Connection Hub APK](https://github.com/MesmerPrism/rusty-quest/releases/download/connection-hub-v0.1.0-alpha.3/rusty-connection-hub-0.1.0-alpha.3.apk)
- [Exact Rusty Hostess companion release](https://github.com/MesmerPrism/rusty-hostess/releases/tag/v0.1.0-alpha.7)

File Manager's CLI can install and launch the exact downloaded APK with its
typed inspected-deployment routes:

```powershell
questionable-file-manager.exe apk install --serial <quest-serial> --file <path-to-connection-hub.apk> --json
questionable-file-manager.exe apk launch --serial <quest-serial> --file <path-to-connection-hub.apk> --json
```

The Hub listener is stopped by default. Its `trusted_lan_experimental`
transport is plaintext and provides no confidentiality; enabling it requires
explicit insecure-transport opt-in and it must be used only on a trusted LAN.

## Other release formats

The exact Labs release also contains a signed MSIX, App Installer feed,
certificate, portable ZIP, CLI archive, provider packages, manifests, hashes,
license, and source pointers. The guided `Setup.exe` is the recommended route
for friends testing on ordinary Windows PCs.

File Manager and Kiosk do not automatically install Rusty Fleet. Fleet remains
a separate advanced preview with its own installer and enrollment workflow;
the **Get Fleet** surface is a verified distribution handoff, not a fleet
controller.
