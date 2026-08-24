# APK diagnostic bundle

`apk diagnose` captures a durable, private evidence pack for one exact local
base APK and one exact Quest serial. It is read-only on the headset and is not
a generic ADB, shell, logcat, dumpsys, screenshot, or bugreport surface.

```powershell
questionable-file-manager apk diagnose `
  --serial <quest-serial> `
  --file <path-to.apk> `
  --output <new-private-folder> `
  --json
```

The output parent must already exist and the named output must not exist. QFM
first admits the local APK immutably and proves its package/version/signer plus
the installed base APK's exact digest and size. It derives the package and its
current-user UID only from fixed Android readback; callers cannot provide a
package name, UID, PID, tag, filter, time range, shell fragment, or ADB
argument. No diagnostic capture runs when this proof fails. QFM verifies the
same exact installed base APK again after the bounded capture set, so package
or install drift prevents publication.

QFM stages all files in a unique sibling directory and atomically publishes the
directory only after the manifest is written. It never overwrites an earlier
bundle. A failed admission, UID proof, final installed-byte proof, cancellation,
or atomic-output write publishes no bundle. A nonzero optional capture is still
published as a partial bundle with its exact exit evidence.

The v2 fixed capture set is:

- `runtime.json`, using `app_runtime_observation.v4`, including the bounded
  separately parsed `mCurrentFocus` and `mFocusedApp` facts from the fixed
  window-manager readback;
- `device.json`, containing only model, Android release, API level, and build
  fingerprint from four fixed properties;
- `package.txt`, from the exact derived package's package snapshot;
- `meminfo.txt`, from the exact derived package's memory snapshot;
- one `logcat-uid-<derived-uid>.txt`, using fixed serial-scoped recent
  `threadtime` logcat filtered only by that derived current-user UID;
- zero or more `logcat-pid-<pid>.txt` files for at most eight PIDs returned by
  the same fixed `pidof <derived-package>` observation; these are optional
  corroboration and never gate the UID capture; and
- `diagnostic-manifest.json`, binding artifact, installed identity, runtime
  summary, derived-UID source, limits, capture semantics, exit status, byte
  count, truncation flag, SHA-256, and observation source for each payload.

Each text payload is bounded to 400 recent log lines where applicable and to
256 KiB after QFM's rendered command metadata. The bundle has at most 14 files
(five fixed captures, up to eight PID corroborations, and the manifest).
Excessive command output is marked truncated and retains a fixed truncation
marker; it does not create an unbounded file. Command stderr is retained only
inside the private payload and is never copied to the public JSON envelope.

The JSON envelope schema is
`questionable.file_manager.apk_diagnostic_result.v2`; the result contract is
`questionable.file_manager.apk_diagnostic_bundle.v2`. Success JSON includes
only sanitized capture metadata, hashes, and authority limitations—not
serials, package names, UIDs, local paths, raw logs, or stderr. A complete
bundle exits zero; a partial bundle exits three. Sanitized error envelopes
contain no private capture data and always report `state_change_possible=false`.

The bundle reports raw transport and Android facts only. Global Android focus
does not establish target-app focus, panel handoff, or readiness: an app-side
owner must interpret it with its retained panel-paused state, advancing
focused/submitted-frame evidence, and its `>=750 ms` stability decision. QFM
never infers application/OpenXR readiness, crash cause, refresh rate, wearer
visibility, or application effect. App/capsule owners consume this evidence
with their own reducer, property profile, hotload fence, OpenXR refresh
request/effective readback, and effective-runtime receipt.

Raw package snapshots and logs can contain private device or application data.
Store bundles in an ignored/private location, and review or sanitize them before
sharing. A Work Environment wrapper owns immutable multi-step run-copy
composition; QFM owns the receipt-pinned identity of this inspected output.
