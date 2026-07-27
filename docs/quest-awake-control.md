# Quest Awake Control

QuestIonAble File Manager owns the exact-serial ADB effects for reviewed Quest
awake control. It exposes two deliberately separate surfaces:

- the ordinary WPF/CLI `device keep-awake` route for one attended local
  headset; and
- a dedicated, hash-pinned Fleet subprocess for managed multi-target
  orchestration.

Neither surface is generic shell or ADB authority.

## Bounded Meta Hold

The local keep-awake route accepts an integer from `60000` through `28800000`
milliseconds: one minute through Meta's eight-hour development hold. Enabling
it applies stay-on, wakes the display, and requests the exact bounded virtual
proximity hold. Confirmation requires independent readback of:

- `mStayOn=true`;
- awake plus display `ON` or `ON_SUSPEND`;
- virtual proximity `CLOSE`;
- the latest `prox_close` broadcast having the exact requested duration and
  positive remaining time.

The route remains pending if any one of those facts differs. A successful ADB
exit code alone is not confirmation. Restoring normal behavior separately
requires stay-on off, autosleep not disabled, and proximity no longer `CLOSE`.

## Watchdog Modes

Fleet can request one of two active modes through the dedicated provider.

`repairOnce` is the effect used by Fleet's Windows watchdog. Each poll reads
first and changes only drifted facts. It does not repeatedly send stay-on,
wake, or proximity commands while their readbacks already match.

`startDeviceWatchdog` installs and starts one fixed File Manager-owned shell
script under `/data/local/tmp`. The script has no caller-supplied command,
path, or process input. It records a generation, current boot identity,
heartbeat, and independent repair counters. Its process identity is checked
before it is reported active. It is a temporary development helper: it is not
installed as an APK, does not start at boot, and is reported ineffective after
a headset reboot.
Reusing the same watchdog generation is idempotent only when the active
helper's polling interval exactly matches the request. A different interval is
rejected rather than silently relabeling the existing helper.

The watchdog interval is bounded from `1000` through `60000` milliseconds.
The proximity hold refreshed by either watchdog remains bounded to eight
hours; watchdog operation extends an attended development session by
reapplying that bounded hold only after drift.

## Stop Is Not Restore

These are distinct typed actions:

- `stopWatchdogs` stops the device helper and leaves the current power and
  proximity settings unchanged. Fleet stops its own Windows loop before it
  calls this action.
- `restoreNormal` first stops the device helper, then explicitly restores
  normal proximity and stay-on behavior.

This separation prevents one operator from silently restoring settings that a
different attended workflow still owns.
Restore fails closed without changing either setting unless fresh process
readback proves the device helper inactive. The provider also rechecks request
expiry after the stop wait and immediately before the restore phase. All other
mutating actions recheck their request window after read-only preflight and
before every individual device mutation.

## Fleet Provider Contract

The dedicated executable is
`questionable-file-manager-awake-provider.exe`, published directly from
`QuestIonAbleFileManager.FleetAwakeProvider`. Its execution route accepts this
exact, case-sensitive argument vector:

```text
integration quest-awake --json
```

Its only description route is the separate exact `--describe-json` vector.
That target-free, non-authorizing response derives its stable action list from
`QuestAwakeContract.Actions` and returns before stdin, provider/controller
factory, ADB, target, or state use. It proves only that the executable can
describe its registry, not that ADB, a target, Fleet authority, or any awake
effect is available.

It reads one strict JSON request capped at 16 KiB. Unknown and duplicate
properties, unsupported actions, invalid identifiers, malformed serials,
expired requests, durations outside one minute through eight hours, and
watchdog intervals outside one through sixty seconds are rejected before ADB
initialization.

The closed action set is:

- `status`;
- `applyBounded`;
- `repairOnce`;
- `startDeviceWatchdog`;
- `stopWatchdogs`;
- `restoreNormal`.

Every successful or pending receipt binds request, operation, preview, Fleet
device, identity revision, action, watchdog generation, duration, and interval.
It reports stay-on, proximity-hold, wake/display, device-watchdog, and
restoration facts independently. The Fleet-facing power projection omits the
ADB serial and controller hardware identifiers. Raw ADB output and command
arguments are represented only by a SHA-256 evidence digest.

Fleet owns target snapshots, operator confirmation, Manifold authorization,
the Windows watchdog lifecycle, scheduling, and the public per-target ledger.
File Manager owns only these exact-serial effects and readbacks.

## Artifact Gate

Publish and validate the Fleet trust unit with:

```powershell
pwsh -NoProfile -File ./tools/Test-FleetAwakeProviderArtifact.ps1
```

The gate produces a self-contained, single-file `win-x64` executable and a
receipt under ignored `artifacts/`. It stages only the executable and a private
`bundle-extract` directory, proves description exits with stdin held open and
a poisoned ADB setting, rejects broad, mixed, extra, and case-varied argument
shapes before ADB discovery, proves the former 24-hour bound is rejected, and
verifies that an isolated framework-dependent apphost cannot substitute for
the published trust unit. Fleet must pin the receipt's lowercase SHA-256 and
use a new private `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for each staged launch.
The canonical release build runs this gate, publishes the executable and
receipt as top-level release assets, and includes both in the app-plus-CLI and
CLI-only portable archives. `release-validation.json` binds their final hash
and validation summary separately from the Kiosk provider.

Host unit tests use a synthetic command runner. They do not contact a headset.
A live Quest validation remains a separate, explicitly approved,
serial-scoped device operation.
