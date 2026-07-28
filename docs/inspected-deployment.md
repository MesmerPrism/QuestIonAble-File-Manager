# Inspected single-device deployment

These routes are a bounded host/operator slice, not a general ADB, shell,
intent, component, or MCP execution surface.

`apk inspect --file <path-to.apk>` uses Android SDK Build Tools (`aapt2` and
`apksigner`) to record file size/SHA-256 and exact package, version, signer, and
split facts. Empty, malformed, ambiguous, multi-signer, and standalone split
inputs fail closed. Direct CLI routes first copy the source into one
identity-bound, non-replaceable admission workspace; hashing, Build Tools, and
ADB receive only that retained copy. The fixed workspace is single-owner,
bounded, and cleans any prior crash residue before reuse.

`apk install` repeats inspection immediately before its serial-scoped install.
It then reads package paths from that exact serial and streams the opened
installed base APK through a hard byte bound without creating a host copy.
Confirmation requires exact streamed base-APK SHA-256/size equality; only then
is the already inspected package/version/signer identity projected as installed.
ADB exit status alone is not confirmation. The receipt identifies the selected
serial plus both expected and installed byte evidence.

`apk launch --serial <quest-serial> --file <path-to.apk>` derives its package
only from the inspected APK, requires matching installed identity, queries the
installed base digest, and size before querying the fixed exported
`MAIN`/`LAUNCHER` surface. It requires exactly one safe component and starts
that result only after the package dump explicitly proves `exported=true`.
Callers cannot supply components, actions, categories, intents,
extras, paths, shell fragments, or generic ADB arguments. Confirmation requires
exact resumed-component readback.

`apk observe --serial <quest-serial> --file <path-to.apk>` returns matching
installed package and byte facts, foreground/top-resumed flags, and package
process IDs from fixed serial-scoped probes. Identity, digest, and size must
match before runtime probes execute. It does not claim OpenXR state or
effective in-app settings.

This slice requires Android Platform Tools and Android SDK Build Tools. It is
single-device and single-base-APK only. Split-set inspected deployment and
app-owned effective-state attestation remain out of scope.

The optional dedicated local API projects only these four inspected-deployment
routes through retained typed commands. See `local-api.md`.
