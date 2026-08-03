# Labs tester onboarding

QuestIonAble File Manager Labs is the guided Windows setup route for the
co-installable Rusty Kiosk Labs preview. The current tested pair is File Manager
`v0.5.0-alpha.7` and Kiosk `v0.6.6-alpha.8`.

## Exact public links

- [Guided File Manager Labs setup](https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/download/v0.5.0-alpha.7/QuestIonAbleFileManager-Labs-Setup.exe)
- [Complete File Manager Labs release](https://github.com/MesmerPrism/QuestIonAble-File-Manager/releases/tag/v0.5.0-alpha.7)
- [Rusty Kiosk Labs tester guide](https://mesmerprism.com/Rusty-Kiosk/#labs)
- [Meta Alpha invite](https://www.meta.com/s/4SlXf1lVo)
- [Exact Kiosk Labs release](https://github.com/MesmerPrism/Rusty-Kiosk/releases/tag/v0.6.6-alpha.8)

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
   Kiosk Alpha.8 core and same-signer setup helper.
6. Follow the [Kiosk Labs tester guide](https://mesmerprism.com/Rusty-Kiosk/#labs)
   for on-headset setup, the Meta Alpha launcher, and the test checklist.

The Kiosk core and Meta launcher are deliberately separate. The Meta launcher
can open a trusted installed Kiosk core, but cannot install, update, provision,
or manage it.

## Other release formats

The exact Labs release also contains a signed MSIX, App Installer feed,
certificate, portable ZIP, CLI archive, provider packages, manifests, hashes,
license, and source pointers. The guided `Setup.exe` is the recommended route
for friends testing on ordinary Windows PCs.

File Manager and Kiosk do not automatically install Rusty Fleet. Fleet remains
a separate advanced preview with its own installer and enrollment workflow.
