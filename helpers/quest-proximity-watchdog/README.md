# Standalone Quest proximity watchdog

This QFM-owned, source-buildable helper maintains an explicitly authorized
Quest development session on the headset through an already authorized ADB
connection. It reuses the established Rusty-XR finite-hold scheduler and
singleton lock; see [source provenance](UPSTREAM.md). It requires no broker,
Fleet identity, Android APK, root, or headset unlock PIN argument. A protected
wearer lock may still require the legitimate wearer interaction.

The default is an eight-hour finite hold, renewed proactively after six hours,
with observations every five seconds plus command execution time. Startup
establishes this process's own successful finite hold even if virtual proximity
already reads `CLOSE`. Only an exit-zero, non-timeout broadcast advances its
monotonic coverage timestamp. Healthy polls do not broadcast again. Failed
attempts retain the old coverage and wait at least one polling interval after
completion before retry; unavailable proximity readback suppresses dispatch.

This is a standalone supervisor, separate from the ordinary one-shot
`device keep-awake` command and from the managed Fleet provider's fixed shell
helper. Those existing routes are unchanged. The eight-hour bound is this
owner's supported limit, not a claim about the universal Horizon OS maximum.

## Build and host verification

Use PowerShell 7.6+, a JDK with `javac --release 8` (JDK 17 is suitable), and
Android SDK Build Tools containing `d8.bat` (API 29 minimum output). No Android
platform JAR, Rusty-XR checkout, Unity installation or network download is
required. Android tools remain separately installed under their own terms.

From the QFM repository root:

```powershell
pwsh -NoProfile -File helpers/quest-proximity-watchdog/tools/Test-Watchdog.ps1 `
  -JdkRoot '<jdk-root>'
pwsh -NoProfile -File helpers/quest-proximity-watchdog/tools/Build-Watchdog.ps1 `
  -JdkRoot '<jdk-root>' -BuildToolsRoot '<android-sdk>/build-tools/<version>'
```

Build runs the host tests first and writes only ignored `build/` outputs:
`outputs/questionable-file-manager-proximity-watchdog.jar` and its source/hash
receipt. Host checks cover establishment on existing CLOSE, proactive renewal,
failure/backoff, cancellation, finite-session expiry, singleton exclusion,
duplicate stop-marker preservation, bounded commands and adapter readback.
They do not establish device compatibility or a currently running process.
This standalone facade has not been deployed or live validated by these host
checks. No prebuilt or published binary is promised by this source addition.

## Start an authorized session

Reserve the exact headset through the caller's machine coordination owner.
Inspect current power/proximity policy, boot and helper processes first.
Preserve a healthy existing owner unless replacement is necessary and
authorized. An already healthy manual hold does not itself authorize replacing
it with a new watchdog. Stop another helper through its own owner and prove its
absence before using this route; do not delete an active lock file.

```powershell
$tool = './helpers/quest-proximity-watchdog/tools/Invoke-Watchdog.ps1'
$adb = '<absolute-path-to-selected-adb.exe>'
$serial = '<exact-quest-serial-or-authenticated-endpoint>'

& $tool -Action start -Adb $adb -Serial $serial -ConfirmDeviceSettings `
  -HoldDurationMs 28800000 -IntervalMs 5000 -UntilStopped
```

Start is an explicit policy mutation: it may enable Android stay-on, wake the
device and issue the finite proximity hold. It requires the built local JAR,
refuses recognized existing helpers, pushes the JAR to its content-addressed
path only if absent, verifies the remote hash,
and backgrounds `app_process`. It never adopts or stops another owner. There
is no implicit build and no `-Disconnect` flag. The old Rusty-XR wrapper's
`-NoBuild` still pushed; this interface's `status` and `stop` never push.

Without `-UntilStopped`, the session lasts `-SessionDurationMs` (default eight
hours). With it, the process continues until explicit stop, process death or
headset reboot; each hold remains finite. It is not reboot persistent, and the
host need not remain connected after startup. Hold/session bounds are
`60000..28800000` ms and poll bounds `1000..60000` ms. Timing must additionally
exceed `2 * (poll interval + 30000ms)` for the command-budget margin;
incompatible short-hold/long-poll combinations fail before device effects.

Treat a `pending` start as possibly effective. Reconcile it with status; never
repeat start simply because a command returned pending or observation failed.
The start confirmation covers successful local hold issuance, current policy
and the owned live process. It does not authenticate a native remaining-time
timer, protected wearer readiness or XR focus.

## Status and readback

```powershell
& $tool -Action status -Adb $adb -Serial $serial
```

Status does not build, push, renew, wake or restore. Before executing the
installed status entry point it verifies its JAR hash against the retained local
build and compares the live process's recorded startup JAR hash. Preserve that
build and receipt for later status. A missing or different
JAR fails closed; inspect the existing owner's provenance rather than pushing
a replacement to obtain status. The status entry point checks the recorded
boot, PID, `/proc` start ticks, SHA-256 of the exact recorded command-line
bytes and exact main-class argument, plus the
process-held lock. `owner_active` reports matching identity and a held lock;
`active` additionally requires active state, and `fresh` requires a recent
monotonic poll. A leftover `.lock` file alone proves nothing.

`last_success_elapsed_ms` is conservative local successful-dispatch time;
`renew_after_ms` is the proactive threshold, not a native remaining timer.
Status also reads current `dumpsys power` and `dumpsys vrpowermanager`
and `dumpsys display` independently. Physical display state comes only from
exactly one `Display Power Controller` with `mDisplayId=0` and
`mIsDisplayInternal=true`, using its own `mScreenState`. Virtual displays,
unscoped power text, missing state and conflicting INTERNAL controllers cannot
supply physical display confirmation. Missing/ambiguous readback stays unknown.
Inspect stay-on, awake, physical display `ON`/`ON_SUSPEND`, virtual
proximity `CLOSE`, live identity, freshness and advancing success timestamps.
Someone wearing the device can cause CLOSE without an override. Missing or
nullable proximity timers alone do not prove expiry.

Android power, virtual proximity, physical display, SensorLock/Guardian or
other protected wearer surfaces, OpenXR focus and camera delivery are separate
observations. `physical_sensor_lock_readiness=not_established` is intentional:
this helper does not dismiss protected UI or infer readiness from Awake/CLOSE.
Verify the relevant window/display and application evidence separately.

Fixed device paths share prefix
`/data/local/tmp/questionable-file-manager-proximity-watchdog` with suffixes
`-<sha256>.jar`, `.log`, `.stop`, `.stop.lock` and `.status`. A live process
records and verifies its startup content-addressed JAR bytes; replacing a local
build or another device JAR cannot relabel that process with a newer hash. Main class is
`com.example.rustyxr.shell.StandaloneWatchdog`. Status includes process identity
but no general command, caller path, network listener or arbitrary shell input.
The append-only log records attempted holds; it is not a periodic heartbeat.
The atomic status file records every completed poll.

The device command budget is 30 seconds: six bounded five-second operations
cover power, physical display, stay-on, wake, proximity read and hold issuance.
The imported renewal formula is unchanged. Default eight-hour/5s polling still
renews after six hours. A 120,000ms hold with 1,000ms polling renews after
58,000ms; a 130,000ms hold at that poll interval renews after 68,000ms. These
short configurations are test windows, not a claim of uninterrupted coverage
under unknown transport/process retirement. Live checks must bind the exact
build and use its reported `command_budget_ms` and `renew_after_ms`.

Command deadlines and output limits bound normal capture. If a timed-out child
cannot be retired, its owner retains the exact process and quiesces: no later
policy command, retry or lease release occurs until exit is observed. A device
watchdog stuck in that exceptional cleanup retains its lock and becomes stale;
preserve custody and reconcile it rather than treating timeout as absence.

## Stop, then restore only when requested

```powershell
& $tool -Action stop -Adb $adb -Serial $serial
```

Stop first verifies the retained local/remote JAR hash and executes its read-only
status entry point to establish the current boot/PID/birth/command-line/lock
owner. It then writes only this helper's fixed marker and waits for its
main-class process to disappear. It never builds, pushes, starts another helper,
kills another process or changes the existing power/proximity policy. Missing
or mismatched owner identity fails closed before the marker. If absence is not
proven, stop remains pending and restore must wait. No generic process-kill or
automatic replacement fallback exists. The marker is checked before further
effects; an already dispatched bounded command may finish before exit.

**Stopping is separate from restoring normal behavior.** When restoration is
authorized, prove all policy-maintaining helpers absent, then use QFM's
existing ordinary typed `device keep-awake --off` route with its required
confirmation/readback, as described in [Quest awake control](../../docs/quest-awake-control.md).
A still-live owner can reapply the policy. Session expiry, process death and
reboot are not restoration receipts.
