# Labs tester onboarding

QuestIonAble File Manager Labs is the guided Windows setup route for the
co-installable Rusty Kiosk Labs preview. The current tested pair is File Manager
`v0.5.0-alpha.9` and Kiosk `v0.6.6-alpha.9`.

## Exact public links

- [Guided File Manager Labs setup](https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/v0.5.0-alpha.9/QuestIonAbleFileManager-Labs-Setup.exe)
- [Complete File Manager Labs release](https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/tag/v0.5.0-alpha.9)
- [Rusty Kiosk Labs tester guide](https://mesmerprism.com/Rusty-Kiosk/#labs)
- [Meta Alpha invite](https://www.meta.com/s/4SlXf1lVo)
- [Exact Kiosk Labs release](https://github.com/MesmerPrism/Rusty-Kiosk/releases/tag/v0.6.6-alpha.9)
- [Exact Fleet Labs release](https://github.com/MesmerPrism/rusty-fleet/releases/tag/v0.1.0-alpha.7)
- [Fleet Labs guided setup](https://github.com/MesmerPrism/rusty-fleet/releases/download/v0.1.0-alpha.7/RustyFleet-Labs-Setup.exe)

Labs prereleases are immutable exact-version releases and are deliberately not
the repository's `latest` stable download.

## First setup

1. Install current
   [Android SDK Platform Tools](https://developer.android.com/tools/releases/platform-tools).
2. Run the guided Labs setup. It explains the project certificate before
   asking Windows to trust it and register the signed Labs app.
3. Enable Developer Mode on the Quest, connect it by USB, put on the headset,
   and accept the USB debugging prompt.
4. In File Manager Labs, select the ready `[USB]` Quest and open
   **Rusty Kiosk**.
5. Confirm the **Labs** channel and select
   **Install and provision (USB)**. The installer embeds the exact signed
   Kiosk Alpha.9 core and same-signer setup helper.
6. Follow the [Kiosk Labs tester guide](https://mesmerprism.com/Rusty-Kiosk/#labs)
   for on-headset setup, the Meta Alpha launcher, and the test checklist.
7. For the optional advanced Fleet preview, open **Get Fleet**, refresh the
   signed Labs release status, and choose the confirmed guided-install route.
   File Manager verifies Fleet's short-lived signed descriptor, exact
   alpha.7 Setup bytes, signer, and non-mutating plan before opening Fleet's
   own installer. Fleet enrollment and Manifold-backed device authority remain
   separate, explicit configuration.

The Kiosk core and Meta launcher are deliberately separate. The Meta launcher
can open a trusted installed Kiosk core, but cannot install, update, provision,
or manage it.

## Other release formats

The exact Labs release also contains a signed MSIX, App Installer feed,
certificate, portable ZIP, CLI archive, provider packages, manifests, hashes,
license, and source pointers. The guided `Setup.exe` is the recommended route
for friends testing on ordinary Windows PCs.

File Manager and Kiosk do not automatically install Rusty Fleet. Fleet remains
a separate advanced preview with its own installer and enrollment workflow;
the **Get Fleet** surface is a verified distribution handoff, not a fleet
controller.
