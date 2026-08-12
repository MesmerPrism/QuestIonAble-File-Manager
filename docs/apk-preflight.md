# APK preflight

`apk preflight --serial <serial> --file <apk> --json` is a fixed read-only
handoff from a source repository to one Quest. The source repository remains
authority for source, dependencies, signing, build commands, artifact choice,
release checks, and app-owned acceptance markers.

Preflight retains the selected APK in the same immutable admission workspace
used by inspected install and deploy. Android SDK Build Tools prove package,
version, signer, SHA-256, size, split state, minSdk, targetSdk, and declared
launcher activities. ADB discovery must return exactly the requested serial.
For a ready serial, a fixed property read supplies the Android API level.

Installed state is one of:

- `absent`: Package Manager reports no installed package;
- `exact`: the opened installed base APK has the same size and SHA-256;
- `different`: installed base bytes are proven unequal or exceed the expected
  artifact size;
- `unverified`: installed layout or byte evidence cannot be established.

Only `exact` installed bytes enable the fixed read-only launcher query and
export proof. `readyForDeploy` requires a ready serial, compatible API, and
exactly one declared launcher. `readyForLaunch` additionally requires exact
installed bytes and one proven same-package exported launcher.
`readyForDiagnose` requires exact installed bytes. These are QFM boundary facts,
not a claim that the app is semantically ready.

The `questionable.file_manager.apk_preflight_result.v1` envelope is complete
when observation succeeds. Exit code `0` means the fixed deploy route is ready;
exit code `3` means observation completed but deploy is not ready; `2` rejects
input; and `1` reports a tool, I/O, or device-read failure. Failure messages are
stable and sanitized. No outcome implies that a headset state change occurred.

The result includes typed argument arrays for the existing `apk deploy`,
`apk launch`, and `apk diagnose` routes. Preflight accepts no package,
component, intent, log filter, build command, shell fragment, or generic ADB
argument.
