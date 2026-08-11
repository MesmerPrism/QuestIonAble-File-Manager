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
the installed base APK's exact digest and size. No diagnostic command runs when
that proof fails. QFM then stages all files in a unique sibling directory and
moves that directory to the requested path only after the manifest is written.
It never overwrites an earlier bundle.

The v1 fixed capture set is:

- `runtime.json`, using `app_runtime_observation.v2`;
- `device.json`, containing only model, Android release, API level, and build
  fingerprint from four fixed properties;
- `package.txt`, from the exact derived package's package snapshot;
- `meminfo.txt`, from the exact derived package's memory snapshot;
- zero or more `logcat-pid-<pid>.txt` files, capped at 400 recent lines for
  each of at most eight PIDs derived from the package readback; and
- `diagnostic-manifest.json`, binding artifact, installed identity, runtime
  summary, device facts, limits, and SHA-256/size/exit evidence for the payload
  files. The returned result also hashes the manifest itself.

Callers cannot provide a package name, PID, log count, log filter/tag, dumpsys
service, property, remote path, component, intent, shell fragment, or arbitrary
ADB argument. Screenshots and bugreports are absent because their breadth and
privacy cost require a separate attended contract.

The JSON envelope schema is
`questionable.file_manager.apk_diagnostic_result.v1`; the result contract is
`questionable.file_manager.apk_diagnostic_bundle.v1`. A complete bundle exits
zero. If a non-authoritative extra capture such as meminfo or one PID log fails,
the bundle is still published with exact exit evidence, `complete=false`, and
exit code 3. Admission, installed-byte, device-read, and atomic-output failures
publish no bundle and return one sanitized failure envelope with
`state_change_possible=false`.

Raw package snapshots and logs can contain private device or application data.
Store bundles in an ignored/private location, and review or sanitize them before
sharing. QFM reports observable facts only; application-specific readiness,
state-machine, media, OpenXR, and release acceptance rules remain owned by the
source repository.
