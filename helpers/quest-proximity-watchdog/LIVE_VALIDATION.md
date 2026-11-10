# Recorded live validation — 10 November 2026

The standalone helper was exercised separately on two Meta Quest headsets over
authorized USB ADB. Both completed normal-policy restoration, a default-mode
smoke test, two proactive renewals in an accelerated finite-hold test, owned
stop without policy restoration, and final default-mode startup. This record
describes those observations; it is not a current-device status report.

## Tested implementation

The tested helper implementation is represented on this publication lineage
by code commit `e7f5076af4991fe6928cb20796722b80574f666b`.
Its helper source, tools, tests and upstream pins were checked byte for byte
against the implementation used for the recorded device tests. Preparing
this publication history did not rebuild or rerun the device artifact.
The locally built DEX JAR was 13,133 bytes, SHA-256
`2e7583cc608ad244f51de4f3add9f8cb83acdbaad8b5e5beb4e1460e9fe0eb85`.
These identify the tested build, not a published binary or a guarantee that
another build has identical bytes. Build and operating instructions remain in
the [helper README](README.md).

The helper reads physical display state from the INTERNAL display-0 power
controller in `dumpsys display`. Virtual display state and the unscoped
`Display Power` text in `dumpsys power` cannot establish physical readiness.
Its command budget is 30 seconds. The imported finite-hold scheduler and
singleton lock remain the implementations identified in [UPSTREAM.md](UPSTREAM.md).

## Observed sequence and results

Each headset was tested through the standard owner routes, with independent
policy/process/display/window observations around the changes:

1. The ordinary QFM `device keep-awake --off` route restored normal policy.
   Independent readback showed stay-on false, virtual proximity `DISABLED`
   and autosleep disabled false, with no maintaining helper present.
2. The standard helper started with an eight-hour hold, five-second polling
   and `UntilStopped`. Its own successful hold, artifact/process identity,
   active/fresh status and independent effective policy were observed.
3. Standard stop proved owned-process absence. Independent readback retained
   stay-on true and virtual proximity `CLOSE`: stopping did not restore policy.
4. Normal policy was restored again before the accelerated test. A fresh
   helper then used a 120,000ms hold, 1,000ms polling and `UntilStopped`.
   Its initial successful hold and two later successful renewals were observed
   on the same owned process and boot, with attempts advancing 1 to 2 to 3
   and no hold failures.
5. After another owned stop and independent no-restore readback, each headset
   started a fresh eight-hour/five-second/`UntilStopped` helper. Both were
   running and active/fresh at the final recorded checkpoint, with a successful
   hold owned by the new process and the six-hour renewal threshold configured.

The accelerated threshold was
`120000 - max(120000 / 4, 2 * (1000 + 30000)) = 58000ms`.
Consecutive successful issuance timestamps gave:

| Test headset | First renewal interval | Second renewal interval | Hold failures |
|---|---:|---:|---:|
| A | 58.324s | 58.433s | 0 |
| B | 58.410s | 58.327s | 0 |

The intervals include polling and command execution time. At sampled
observations, power was `Awake`, physical INTERNAL display state was `ON`,
stay-on was true and virtual proximity was `CLOSE`. Independent window/readiness
checks observed no active SensorLock, and the foreground application actors
remained unchanged across the power-policy tests. Helper status itself continued
to report `physical_sensor_lock_readiness=not_established`; separate observations
supplied the window/readiness evidence.

Raw command stdout/stderr, exit/stream-retirement evidence, artifact and process
identities, action logs and independent policy observations were joined for
this record. Machine/device identifiers and raw captures are not public assets.
Earlier smoke testing exposed an incompatible power-display format; the tested
commit above includes the physical INTERNAL display parser fix. The earlier
pending result is not counted as a successful test of the corrected build.

## Default six-hour renewal on Horizon OS v209

On 10 November 2026, the same helper implementation was observed on two
Quest 3S headsets after their system update to v209.0.0.174.430. Each new
post-update session used the normal eight-hour finite hold, six-hour proactive
renewal threshold, five-second polling, 30-second command budget and
`UntilStopped` lifetime.

Read-only status observations before and after the threshold verified the same
helper process, boot and loaded artifact for each headset. Hold attempts
advanced from 1 to 2, successful monotonic issuance timestamps advanced, and
hold failures remained zero:

| Test headset | Successful renewal interval | Beyond six-hour threshold | Hold failures |
|---|---:|---:|---:|
| A | 21,600,678ms | 678ms | 0 |
| B | 21,601,668ms | 1,668ms | 0 |

Both renewals occurred before the previous eight-hour hold's locally accounted
expiry. At the sampled bookends, status was active/fresh, power was `Awake`,
the physical INTERNAL display controller was `ON`, stay-on was true and
virtual proximity remained `CLOSE`. Observation used status only and did not
change power policy. This establishes an actual default-threshold renewal
on each tested headset, including renewal while desired readback stayed healthy.

These later v209 observations are separate from the earlier accelerated
120-second-hold tests. The system update interrupted the earlier long session;
the two sessions are not one continuous soak. Coverage is calculated from
successful bounded dispatch timestamps, not an authenticated native hold timer.
The v209 status continued to report
`physical_sensor_lock_readiness=not_established`: this observation does not
establish physical SensorLock, camera or XR readiness. It does not establish a
complete eight-hour/overnight soak, reboot persistence, host-disconnect survival
or wearer-approval bypass.

## Reproducing the bounded checks

Follow the README's artifact, authorization, existing-owner and readback rules
for one selected headset at a time. Qualify and retain the exact local build
and ordinary QFM CLI before using these example commands:

```powershell
$tool = './helpers/quest-proximity-watchdog/tools/Invoke-Watchdog.ps1'
$qfm = '<qualified-ordinary-qfm-cli>'
$adb = '<absolute-path-to-selected-adb.exe>'
$serial = '<exact-selected-quest-serial>'

# Restore normal policy only after all maintaining owners are proven absent.
& $qfm device keep-awake --serial $serial --off `
  --confirm-device-settings --json --adb $adb

# First verify the default start/status/stop route and independent readback.
& $tool -Action start -Adb $adb -Serial $serial -ConfirmDeviceSettings `
  -HoldDurationMs 28800000 -IntervalMs 5000 -UntilStopped
& $tool -Action status -Adb $adb -Serial $serial
& $tool -Action stop -Adb $adb -Serial $serial

# Confirm absence and unchanged policy, then restore normal before this test.
& $qfm device keep-awake --serial $serial --off `
  --confirm-device-settings --json --adb $adb
& $tool -Action start -Adb $adb -Serial $serial -ConfirmDeviceSettings `
  -HoldDurationMs 120000 -IntervalMs 1000 -UntilStopped
& $tool -Action status -Adb $adb -Serial $serial
```

Observe standard status and independent readback until three distinct successful
elapsed timestamps establish the initial hold plus two renewals. Use a finite
observation deadline; the recorded test used 180 seconds from short-start
dispatch. A pending or unknown start may already be effective: reconcile its
owned status rather than submitting another start.

The accelerated hold replaces a longer existing hold with a two-minute window.
Prepare the normal configuration and recovery decision before shortening it.
An observation deadline does not guarantee coverage if a transport or owned
child stalls. Preserve unresolved process custody; do not bypass it with another
start, arbitrary kill or restore. After successful observation, use standard
stop, prove absence and no-restore readback, then issue one authorized default
start as shown above. Status and stop do not build or push.

## What this record does not establish

The earlier bounded tests observed two accelerated renewals and final default
startup; the later v209 observation above adds one actual six-hour default
renewal per headset. No complete eight-hour or overnight soak was established.
Successful bounded dispatch timestamps provide local coverage accounting,
not an authenticated native remaining-hold timer or continuous physical-readiness
proof between observations.

No reboot persistence, host-disconnect survival, induced midrun drift recovery,
wearer-approval bypass, camera/media/transport health or universal Horizon OS
compatibility is established. Protected wearer surfaces remain legitimate human
decisions. The historical host build receipt remains unchanged with
`runtime_validated=false`; this separate dated record carries the live evidence
scope. A later session still requires fresh owner, artifact, process and
effective-state readback.
