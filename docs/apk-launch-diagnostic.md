# APK launch diagnostic

`apk launch-diagnose` is the agent-only exact-once launch route for retaining
the first bounded UID log bytes of one inspected APK. It is deliberately
separate from read-only `apk diagnose` and ordinary `apk launch`.

```powershell
questionable-file-manager apk launch-diagnose `
  --serial <quest-serial> `
  --file <path-to.apk> `
  --output <new-private-folder> `
  --json
```

The output parent must exist and the output name must not. QFM admits the APK
through its immutable retained copy, proves exactly one ready selected serial,
requires one standalone installed APK with no splits and exact complete bytes,
derives one exported launcher and the
current-user package UID (rejecting shared-UID packages), and reads a fixed
device epoch fence. It then starts
one continuous `logcat` capture fixed to epoch rendering, that fence, and that
derived UID. Only after the capture process starts does QFM recheck the exact
ready serial and installed bytes and issue one resolved launcher dispatch.

The command accepts no package, UID, PID, tag, log filter, capture duration,
component, intent, action, category, extra, command, shell fragment, or raw ADB
argument. It has no WPF or local-API projection and performs no screenshot,
recording, bugreport, install, permission mutation, property mutation, stop, or
retry.

## Evidence and cleanup

The capture is limited to 256 KiB and a fixed ten-second post-action window.
The runner kills the capture process tree and drains its streams before QFM
continues. Both the initial drain and the cancellation-plus-pipe-revocation
terminal join are finite. If a cancellation-resistant drain still owns the
pipe, QFM fails without hashing or publishing a bundle rather than waiting
without a bound or returning bytes that could still change. QFM then rechecks
exact installed bytes and the derived UID. Current
package PIDs come from one fixed process inventory and must match both that UID
and an exact package or `package:process` name. A unique sibling stage writes
`logcat-uid-post-fence.txt` and `launch-diagnostic-manifest.json` with
create-new semantics, and only a complete manifest is published by an atomic
directory move.

If another creator races publication after the launch dispatch, QFM never
overwrites or deletes either directory. The complete closed bundle remains in
its collision-safe sibling, the sanitized result reports that sibling leaf,
and the overall disposition is `outcomeUnknown`.

The manifest binds:

- immutable local artifact identity and before/after installed-byte identity;
- host launch-fence ID/time and device epoch fence;
- before/after current-user UID;
- the exact resolved launch result and current package PIDs;
- capture byte count, SHA-256, exit, truncation/early-exit, window, and process-
  tree cleanup facts; and
- the effect disposition before final-path publication and explicit semantic
  limitations.

`questionable.file_manager.apk_launch_diagnostic_result.v1` is the sanitized
CLI envelope. The private bundle contract is
`questionable.file_manager.apk_launch_diagnostic_bundle.v1`. `completed`
requires exact installed-byte/UID readback, resumed-component evidence, a
current UID-bound package PID, the full capture window, no byte-limit hit, and
successful process-tree cleanup. `launchPending`, `rejectedBeforeDispatch`, and
`outcomeUnknown` remain distinct.
The mutation receipt is dispatch-aware: a predispatch rejection contains only
the terminal `rejected` transition, while `sent` and `pending` begin immediately
before the one fixed `am start` dispatch. Any uncertainty after that boundary
remains `pending`. An exception after dispatch carries that same receipt through
the CLI failure envelope instead of collapsing it into a receiptless error.
Capture drains use cancellation-bound writes; a bounded terminal drain failure
revokes process pipes, the destination stream, and the digest before return, so
late work cannot append to or re-hash retained evidence.
None proves app readiness, OpenXR readiness, app-owned freshness, semantic
acceptance, or wearer visibility. An app/capsule owner consumes the raw UID log
bytes with its own launch fence and reducer.

Raw UID logs and exact target/artifact identities are private evidence. Keep
the output ignored and review it before sharing.
