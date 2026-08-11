# Agent Quest APK workflow

This is the public, agent-readable handoff between a source repository and
QuestIonAble File Manager. File Manager owns bounded Quest/ADB effects; the
source repository owns source, dependencies, signing inputs, build commands,
and the choice of build output.

## Fast iteration loop

1. Read the source repository's tracked agent instructions and use its owned
   build command. Do not make File Manager infer or reproduce that build.
2. Select one explicit `.apk` output and one explicit Quest serial.
3. Run `apk inspect --file <apk> --json` when a read-only admission check is
   useful before any device change.
4. Run `apk deploy --serial <serial> --file <apk> --json`. Installation options
   are limited to `--no-replace`, `--downgrade`,
   `--grant-runtime-permissions`, and `--test-only`.
5. Treat the returned mutation receipt and install/launch/runtime evidence as
   separate facts. A confirmed deploy proves exact installed bytes and the
   immediate resolved-component readback; runtime fields report what was seen,
   not application-owned semantic readiness.
6. Use `apk observe --serial <serial> --file <apk> --json` for later read-only
   checks against the same artifact.

For a release candidate, add the source repository's release gates before
step 4. The QFM device boundary stays the same; validation depth is selected by
the source/release lane rather than by accepting broader ADB commands.

## Fixed QFM actions

QFM provides typed routes for device discovery, file transfer, APK inspection,
exact install, composite deploy, resolved launch, runtime observation, package
export, bounded power/performance control, Wi-Fi ADB setup, and the documented
Kiosk/Fleet integrations. Prefer those routes over reimplementing their ADB
sequences.

## Information that remains project-owned

Keep these facts in tracked source-repository instructions or build manifests:

- build and test commands;
- expected APK output location and build variant;
- package/channel policy and signing authority;
- app-owned readiness markers, logs, state files, or validation scenarios;
- release-only checks and rollback expectations.

These facts vary by project and cannot safely become one generic QFM command.
QFM may consume an explicit artifact and report device facts, but it must not
guess a repository, invoke arbitrary build scripts, accept raw shell fragments,
or claim app-owned semantics.

## Diagnostic boundary

The fixed `apk observe` result reports installed byte identity, processes,
foreground components, top-resumed components, and known blocking Quest system
components. A future diagnostic-bundle route may package additional fixed,
bounded evidence such as recent package-scoped logs and device state. Until
that contract lands, project-specific diagnostic interpretation belongs in the
source repository's tracked agent instructions; this document is not authority
to run arbitrary `adb shell` or unconstrained `logcat` commands.
