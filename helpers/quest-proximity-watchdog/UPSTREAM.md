# Upstream source and license

This helper reuses the MIT-licensed Rusty-XR shell watchdog from
[MesmerPrism/Rusty-XR](https://github.com/MesmerPrism/Rusty-XR), directory
`examples/quest-broker-shell-helper`, at source commit
`00a4716b5dcd9fe9b38c3a7eb2aea648dbe76181`, tree
`c6fa02903639ac5bcb5d4cf507efe28867bb15d5`. That revision was locally reviewed;
its availability on the upstream public remote is not asserted. All required
source is included here, so neither that remote revision nor a separate Rusty-XR
checkout is needed to build or use this helper.

`ProximityHold.java`, `ProximityWatchdogLease.java` and `ProximityHoldTest.java`
are imported byte for byte. `upstream-pins.json` records their SHA-256 hashes,
checked by `tools/Test-Watchdog.ps1`. Preserve their line endings when editing
unrelated files. The upstream MIT copyright and permission notice are in
`LICENSE`, also packaged in the built DEX JAR.

`StandaloneWatchdog.java` adapts the pinned `Helper.java` watchdog loop, fixed
power/proximity commands, readback patterns and bounded process execution. It
adds a closed standalone entry point, fixed QFM-specific state paths, process
identity/status, and independent host readback. It omits the upstream broker,
camera, video and foreground-control features. `WatchdogInfrastructureTest.java`
adapts the upstream timeout, cancellation and process-lock tests to this facade.
The scheduling formula, successful-dispatch timestamp, failed-attempt backoff,
session stop checks and singleton-lock implementation remain upstream-owned
code, rather than a second scheduler.

The historical upstream JAR was 45,523 bytes with SHA-256
`7bca280fccc1177a6dd8f2b4fd7cce7a4bad23c242630e724e62f1e5d784210b`.
It is not distributed here. A QFM standalone build produces a different JAR;
its generated `build/outputs/build-receipt.json` binds the actual output to its
source hashes and reports `runtime_validated=false`. Historical device results
for the upstream helper do not establish live validation of this facade.
