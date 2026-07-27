# Release Workflow

GitHub Pages is the stable human-facing install surface. GitHub Releases is the
binary source of truth. Pages links target `releases/latest/download`, so a new
release does not require new website URLs.

## Published Assets

Every Windows preview release contains:

- `QuestIonAbleFileManager-Setup.exe`: signed guided installer and updater;
- `QuestIonAbleFileManager-win-x64.msix`: signed Windows package;
- `QuestIonAbleFileManager.appinstaller`: update feed for the stable package
  identity;
- `QuestIonAbleFileManager.cer`: public half of the package signing certificate;
- `QuestIonAbleFileManager-win-x64.zip`: portable WPF app plus operator CLI;
- `questionable-file-manager-cli-win-x64.zip`: CLI-only automation archive;
- `questionable-file-manager-kiosk-v2-provider.exe`: dedicated self-contained
  single-file provider for Fleet;
- `questionable-file-manager-kiosk-v2-provider.receipt.json`: isolated-smoke
  result, byte length, and lowercase SHA-256 for the provider;
- `questionable-file-manager-awake-provider.exe`: dedicated self-contained
  single-file Quest awake effect-owner provider for Fleet;
- `questionable-file-manager-awake-provider.receipt.json`: isolated rejection,
  trust-unit shape, byte length, and lowercase SHA-256 for the awake provider;
- `questionable-file-manager-connectivity-provider.exe`: dedicated
  self-contained single-file Quest connectivity execution provider for Fleet;
- `questionable-file-manager-connectivity-provider.receipt.json`: isolated
  absent-profile smoke, trust-unit shape, byte length, and lowercase SHA-256;
- `SHA256SUMS.txt`: checksums for every release asset;
- `release-validation.json`: signature, timestamp, identity, feed, and all
  dedicated Fleet-provider validation summaries.

Releases from `0.4.0` also contain byte-identical former-name aliases for the
setup, MSIX, App Installer, certificate, portable archive, and CLI archive.
Those assets are an update bridge for existing `0.3.x` installations and pinned
automation; they are not the canonical product names. The portable archives
similarly include the former CLI executable name as a deprecated alias.

Android Platform Tools are not bundled. The app discovers an operator-supplied
`adb.exe` through the documented search order.

Both portable archives include
`questionable-file-manager-kiosk-v2-provider.exe`, the dedicated
self-contained single-file Fleet catalog provider published directly from
`QuestIonAbleFileManager.FleetKioskV2Provider`, and its validation receipt. The
release never renames or repackages the general operator CLI as this provider.
Fleet pins the receipt's lowercase SHA-256, copies only that EXE to an empty
private stage, creates only its private `bundle-extract` subdirectory, and
rejects the ordinary framework-dependent build apphost as a provider trust
unit. Every staged launch uses a new private
`DOTNET_BUNDLE_EXTRACT_BASE_DIR`; native runtime extraction never shares a
mutable cross-stage cache.

They also include `questionable-file-manager-awake-provider.exe`, published
directly from `QuestIonAbleFileManager.FleetAwakeProvider`, and its artifact
receipt. The release gate requires the same isolated single-file trust-unit
shape, strict former-bound rejection before ADB initialization, empty standard
error, and failed isolated framework-dependent apphost control. The release
validation receipt records the provider hashes and validation summaries
separately.

They also include `questionable-file-manager-connectivity-provider.exe`,
published directly from
`QuestIonAbleFileManager.FleetConnectivityProvider`, and its artifact receipt.
Its isolated gate accepts only `integration quest-connectivity --json`, fails
closed when the File Manager-owned current-user profile is absent, rejects
broad CLI shapes before initialization, and proves the framework-dependent
apphost cannot substitute for the pinned single-file trust unit. The release
validation receipt records its hash separately from the awake and Kiosk
catalog providers.

The WPF app, automation CLI, guided setup helper, MSIX package, and GitHub Pages
site use the same folder mark. Its canonical source and multi-resolution ICO
live under `assets/branding`; `tools/app/New-BrandAssets.ps1` regenerates every
committed application and website size, and `tools/app/Test-BrandAssets.ps1`
checks the assets plus embedded EXE resources.

## Consumer Routes

The recommended route is `QuestIonAbleFileManager-Setup.exe`. It downloads the
public certificate and App Installer feed from the latest GitHub release,
requests Windows administrator approval, trusts the certificate in Local
Machine `TrustedPeople`, installs or updates the stable package identity, and
launches the app.

For machines that block the self-issued helper executable, the manual fallback
is deliberately kept public:

1. download `QuestIonAbleFileManager.cer` and trust it in **Trusted People**;
2. download and open `QuestIonAbleFileManager.appinstaller`;
3. if App Installer is unavailable, download and open the signed MSIX;
4. use the portable ZIP when package installation is restricted.

A self-issued certificate can support an explicitly trusted MSIX but does not
guarantee that Smart App Control will admit a downloaded helper executable.
The website must not describe this helper as Smart App Control safe.

## Local Release Validation

PowerShell 7.6 or newer and Visual Studio with the MSIX/Desktop Bridge workload
are required. Export a repository-specific PFX from the private Windows
certificate store into ignored `artifacts/signing`, then run:

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

For a release that exposes the optional Fleet download handoff, the release
owner must also supply all six public trust inputs:

```powershell
-FleetInstallerReleaseDescriptorUri 'https://mesmerprism.com/Rusty-Fleet/metadata/<channel>/release.json' `
-FleetInstallerDescriptorPublicKeySpkiBase64 '<canonical-public-rsa-spki>' `
-FleetInstallerDescriptorSignerSpkiSha256 '<lowercase-spki-sha256>' `
-FleetInstallerSetupSignerCertificateSha256 '<lowercase-setup-certificate-sha256>' `
-FleetInstallerChannel '<channel>' `
-FleetInstallerStateRootRelativePath 'QuestIonAbleFileManager/FleetInstaller'
```

Append those arguments to `Invoke-ReleaseBuild.ps1`; do not store real
publisher values or local paths in this repository. They are all-or-none and
are embedded as versioned assembly metadata. The script clears ambient MSBuild
values, validates the SPKI and pins, and restores its caller's environment.
Omitting all six produces an inert release; a partial set fails. The canonical
Pages location publishes only short-lived signed `release.json` metadata. Its
v2 payload must bind
`https://github.com/MesmerPrism/rusty-fleet/releases/download/v<version>/RustyFleet-Setup.exe`;
never publish or derive a Pages-sibling Setup binary.

The default consumer test exercises the elevated guided route. On an
unattended, non-elevated agent shell, use `-DirectPackage` to validate the
helper's no-change plan and then install the same signed MSIX directly; the
receipt records which route ran and never claims the guided route passed.
The launch probe allows 60 seconds by default because Windows can spend more
than 20 seconds validating a newly installed package on its first activation;
use `-LaunchTimeoutSeconds` only when a slower validation environment needs a
larger bounded window.

The release build preserves the native WAP-produced MSIX, applies SHA-256
Authenticode signatures with RFC 3161 timestamps, verifies the expected
publisher, keeps the signed `MesmerPrism.MetaQuestFileManager` package identity
for in-place updates, checks the App Installer identity and stable URLs, inspects the MSIX
payload, and writes checksums. Before packaging, it resolves the published
Kiosk tag to an exact commit and verifies the bundle version, source pointer,
all declared byte counts and SHA-256 values, and both APK signer digests. The
public validation receipt records that Kiosk provenance alongside the Windows
signatures and public release filenames, never local or CI-runner build paths.
It also isolation-tests both exact dedicated Fleet provider executables with no
unexpected sibling files. The Kiosk provider must return the strict
absent-profile response while preserving its existing broad/general negative
vectors. The awake provider must reject the former 24-hour bound and preserve
its existing broad and case-varied negatives before ADB discovery. Awake,
connectivity, and Kiosk provider gates additionally require the exact
`--describe-json` route to exit while stdin remains open, with poisoned backend
settings, empty standard error, exact registry actions, and no authority or
target claim. Each gate also requires the descriptor's provider version,
derived from immutable Core build metadata, to equal the semantic version
passed to that artifact's publish. Mixed, extra, and case-varied description
shapes reject. All three require an unreachable general CLI dispatcher and
rejection of an isolated framework-dependent apphost before either portable
archive is created.
Existing release assets are not overwritten; any payload change requires a new
semantic version. The consumer test stages a local HTTP feed with range
support because the Windows deployment service does not consume workspace file
URIs like a browser download.

The guided setup also exposes a no-change agent route:

```powershell
QuestIonAbleFileManager-Setup.exe --plan --json
```

`--plan` downloads and validates the release identity without trusting a
certificate or installing a package. Actual guided installation requests UAC;
the elevation is part of the public installer contract.

## GitHub Configuration

The release workflow requires these Actions secrets:

- `WINDOWS_PACKAGE_CERTIFICATE_BASE64`;
- `WINDOWS_PACKAGE_CERTIFICATE_PASSWORD`;
- `WINDOWS_PACKAGE_PUBLISHER`;
- `WINDOWS_PREVIEW_SETUP_CERTIFICATE_BASE64`;
- `WINDOWS_PREVIEW_SETUP_CERTIFICATE_PASSWORD`.

Optional Actions variables select alternate RFC 3161 timestamp services:

- `WINDOWS_PACKAGE_TIMESTAMP_URL`;
- `WINDOWS_PREVIEW_SETUP_TIMESTAMP_URL`.

Private keys stay in the Windows certificate store, ignored local artifacts,
and encrypted GitHub Actions secrets. They are never committed. Publishing a
tag or manually dispatching the workflow builds, validates, uploads, and then
creates a new matching GitHub Release.
