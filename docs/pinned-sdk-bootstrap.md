# Pinned SDK bootstrap

QFM source builds require the exact SDK in `global.json`: `10.0.302` with
`rollForward` set to `disable`. Use this host-only route instead of choosing a
nearby SDK from `PATH`:

```powershell
pwsh -NoProfile -File ./tools/Build-QfmCli.ps1
```

The route reads the checked-in pin, requires a clean Git source tree, and calls
the selected `dotnet.exe` by absolute path. It first accepts only an exact,
runtime-complete SDK from its stable per-user cache, an explicitly configured
`DOTNET_ROOT`, or Windows Program Files. It never selects an SDK by `PATH`
ordering. A wrong or damaged cache is a failure, not a reason to substitute a
different SDK.

When the SDK is absent, the route downloads the Microsoft portable
[`dotnet-install.ps1`](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script)
from its official stable endpoint, verifies QFM's reviewed SHA-256, invokes it
with the exact version, `-InstallDir`, `-Architecture x64`, and `-NoPath`, then
requires both `dotnet --version` and `Microsoft.NETCore.App 10.0.10` readback.
The local cache is under the current user's LocalApplicationData directory by
default. Pass `-CacheRoot <local-directory>` only when an operator needs a
different machine-local cache; do not commit that directory.

The published CLI is stored beneath a content-addressed cache directory keyed
by the clean Git tree, SDK version, and architecture. Its adjacent
`qfm-cli-build-receipt.json` records source commit/tree, the `global.json` hash,
SDK/runtime facts, executable and output-tree hashes, and the smoke result. A
missing, malformed, or hash-mismatched cache entry fails closed and is retained
for inspection. Download access is only bootstrap transport; the receipt does
not treat network or device access as build acceptance evidence.

Microsoft currently lists .NET 10 as LTS and SDK `10.0.302` as the supported
10.0 SDK release. Update QFM's `global.json`, owned CI inputs, the reviewed
installer hash, and expected runtime together only through a separately
reviewed servicing change. See [Microsoft's .NET 10 downloads](https://dotnet.microsoft.com/download/dotnet/10.0)
and [support policy](https://learn.microsoft.com/dotnet/core/releases-and-support).
