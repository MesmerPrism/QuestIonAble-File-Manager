# Optional browser connector development archives

`tools/app/Publish-BrowserConnector.ps1` packages the optional browser connector
from the current source checkout. It produces an **unsigned development/source
archive**, separate from the signed Windows release, installer, update feed and
WPF application. It changes no release version or trust configuration.

The archive contains the untrimmed .NET 10 BrowserHost and shared Core DLLs,
their dependency/runtime configuration, the complete public `wwwroot` shell,
self-hosted vendor bundles and their notices/package inventories, repository
`LICENSE`, a file manifest and SHA-256 checksums. Android Platform Tools, Kiosk
APKs, Fleet providers, credentials, retained state and private keys are absent.
ADB remains operator-supplied. Bundled vendor reproduction inputs remain in
[`tools/browser-usb`](../tools/browser-usb/README.md) and
[`tools/browser-download`](../tools/browser-download/README.md).

## Build an archive

Requirements: PowerShell 7.6+, Git, the exact .NET 10 SDK pinned by
[`global.json`](../global.json), and ordinary trusted source/build inputs. The
script uses the standard `dotnet` host under process-local `DOTNET_ROOT` when
set, otherwise the application found on `PATH`. It checks the selected SDK
version from the repository directory. There is no executable-path, project,
publish-profile or extra-MSBuild-argument override.

From the repository root, create an output **parent** if needed, then choose a
new leaf directory:

```powershell
New-Item -ItemType Directory -Path ./artifacts -Force | Out-Null
pwsh -NoProfile -File ./tools/app/Publish-BrowserConnector.ps1 `
  -RuntimeIdentifier win-x64 `
  -OutputDirectory (Join-Path (Get-Location) 'artifacts/browser-connector-win-x64-dev')
```

The only parameters are `RuntimeIdentifier` and `OutputDirectory`. Both are
required for execution. The closed RID set is:

| RID | Runtime required on the destination |
| --- | --- |
| `win-x64`, `win-arm64` | Windows .NET 10 and ASP.NET Core 10, matching architecture |
| `linux-x64`, `linux-arm64` | Supported glibc Linux .NET 10 and ASP.NET Core 10, matching architecture and native prerequisites |
| `osx-x64`, `osx-arm64` | Supported macOS .NET 10 and ASP.NET Core 10, matching architecture |

These are publish targets, not a claim that every target has been run. Musl,
other architectures and self-contained deployment are not included in this
slice. The archive has no native apphost; launch its DLL through `dotnet`.
See Microsoft's [framework-dependent deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
for the runtime distinction. The SDK's normal restore may access configured
package sources; the script installs no tools or runtimes.

Use an absolute local output path whose parent already exists and has no
symlink/junction/reparse ancestors. Within the source checkout it must be below
ignored `local/` or `artifacts/`. Wildcard/control characters and Windows UNC
paths are rejected. Existing output is never overwritten, including an earlier
failed attempt. Choose another new leaf to retry explicitly.

Publish arguments fix Release/net10.0, framework-dependent DLLs, no trimming,
single-file, AOT, ReadyToRun or debug symbols. Static compression is disabled;
the host serves its original shell bytes through its existing static-file and
service-worker contracts. Unused SDK endpoint metadata and vendor
`.gitattributes` are not archived. No project file is rewritten.

## Output and provenance

Successful output contains:

```text
questionable-file-manager-browser-<rid>-framework-dependent-dev.zip
SHA256SUMS.txt
payload/             # raw publish output, not the distribution unit
.build/              # local build/restore intermediates, not for distribution
```

Distribute only the ZIP and its external checksum file. Build intermediates may
contain local paths. An `.incomplete` marker remains on failures; do not use that
attempt as a completed package. The script never recursively deletes an output.

Inside the ZIP, `connector-package.json` uses
`questionable.file_manager.browser_connector_package.v1`. It records the RID,
framework-dependent deployment, exact SDK version, `signedRelease:false`,
current Git commit/tree, an explicit whole-worktree dirty flag, and SHA-256/size
records for scoped source/build/vendor inputs and every payload file. It checks
the source snapshot again after publish and rejects changes in those inputs.
The dirty flag includes unrelated untracked changes without publishing their
names or contents. It never assumes the checkout is clean.

The manifest lists payload files; inner `SHA256SUMS.txt` additionally hashes the
manifest. The checksum file does not hash itself. The external checksum hashes
the ZIP. Archive entry order and metadata use fixed ordering/timestamps for
identical admitted inputs. This does **not** establish reproducible compiler
output or an authenticated release: SDK/runtime provenance, ambient build
configuration and a dirty checkout still need the consumer's review. Hashes
detect byte changes; they provide no signature or publisher trust.

The content gate requires the exact current shell manifest, byte-identical
static files and license, both vendor notices/inventories, the requested RID
and .NET 10 framework references. Unexpected publish files and dependencies
fail closed. Review and update the script when the actual project dependency
or packaging contract intentionally changes.

## Verify and smoke the produced package

Run the isolated fixture suite without publishing or starting a host:

```powershell
pwsh -NoProfile -File ./tools/app/Test-BrowserConnectorPackage.ps1
```

Verify the external ZIP hash against `SHA256SUMS.txt`, then extract into a new
empty directory. Verify the inner checksum inventory there, including
`connector-package.json`. On Linux/macOS with the corresponding utility:

```sh
sha256sum -c SHA256SUMS.txt
```

On PowerShell, compare each listed hash with `Get-FileHash -Algorithm SHA256`.
Keep the external and extracted checksum files in their separate directories.

An inert smoke from an unrelated working directory is:

```powershell
dotnet <extracted-directory>/questionable-file-manager-browser.dll
```

It must print usage and exit 2 without creating a listener, initializing ADB or
requiring a source checkout. This checks dependency loading/rejection only.
Actual startup/static/CSP/offline checks must run the **extracted package** on
each claimed host runtime, using an explicitly reserved test port where needed.
Those checks are separate from the fixture suite and publishing.

For deliberate use, follow the private bearer and exact numeric loopback
listener instructions in [`browser-operator.md`](browser-operator.md). Supply
the listener explicitly; no credential belongs in a URL, browser storage,
archive, service worker, log or screenshot. Publishing does not launch or
configure a service. Retained APK authority remains opt-in with its existing
protected state/key contracts; the package creates no default owner state.

Windows protected artifact staging requires NTFS and its existing owner safety
checks. Linux/macOS currently expose explicit unsupported staging/secure-owner
capabilities with the Windows companion/CLI alternative. Connectivity profiles
and Fleet installer services also retain their Windows boundaries. An archive
for those platforms does not promise full cross-platform feature parity or
macOS runtime validation. The existing CLI/WPF remains available.
