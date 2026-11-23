# Third-Party Notices

The repository currently builds from the .NET SDK and NuGet test packages.
Release artifacts must include the notices required by their resolved
dependencies.

Android SDK Platform Tools, including `adb`, are not bundled. When selected by
the user, they remain subject to Google's Android SDK terms and notices.

Meta Quest names and trademarks belong to Meta Platforms, Inc. This project is
independent and is not affiliated with or endorsed by Meta.

The source-only standalone proximity watchdog in
`helpers/quest-proximity-watchdog` incorporates MIT-licensed Rusty-XR scheduler,
lock and tests, copyright (c) 2026 MesmerPrism. Its upstream permission notice,
exact source provenance and adaptations are retained in that directory's
`LICENSE`, `UPSTREAM.md` and `upstream-pins.json`. The standalone build packages
the license and provenance with its DEX JAR.

Official Windows release bundles may aggregate the optional Rusty Kiosk Android
application and its same-signer setup helper. Rusty Kiosk is a separate work
licensed under GNU AGPL-3.0-or-later. Its APK bundle includes
`RUSTY-KIOSK-LICENSE.txt`, `RUSTY-KIOSK-SOURCE.txt`, and a hash manifest. The
corresponding source is available at https://github.com/MesmerPrism/Rusty-Kiosk.
The QuestIonAble File Manager remains useful without installing or using Rusty
Kiosk.
