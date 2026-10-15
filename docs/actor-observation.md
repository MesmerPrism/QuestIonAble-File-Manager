# Closed device actor observation

The agent-only read-only route is:

```text
questionable-file-manager device actor-observe --serial <exact-serial> --pid <positive-decimal> --json
```

It accepts exactly those arguments, with no package, path, shell, duration,
discovery, wake, launch, app-provider, lease or daemon operation. The selected
ADB executable uses the existing `QUESTIONABLE_FILE_MANAGER_ADB` configuration.
The shared `OperatorCommands.ObserveDeviceActor` route calls
`AdbClient.ObserveDeviceActorAsync`. No WPF, local API or Fleet projection is
added. `operator-actions --json` and CLI help advertise the agent route.

One bounded serial-scoped `exec-out sh -c` invocation runs a fixed program in
this order: `getprop ro.serialno`, boot ID read, complete numeric
`ps -A -o UID,PID,PPID,NAME,ARGS`, and the requested PID's proc stat/status.
Only a validated positive integer is substituted into the two fixed proc paths.
Every component's exit status is checked; any failed read stops the group.
This is a sequential observation, not an atomic process snapshot.
ADB exec-out may itself return zero after copying a failed remote command's
output; a failed component header still refuses the observation. On that path
the retained stream includes prior successful payloads and the failed header,
not the failed read's stdout discarded by the fixed program.

The device frame is `QFM_ACTOR_V1` followed by exactly five ordered
`name:exit-code:byte-count` headers and exact-length UTF-8 payloads, separated
by one LF, then `END` and EOF. A sentinel preserves the reads' trailing
newlines inside shell command substitution. Length framing prevents process
arguments from forging section boundaries. Each successful payload is returned
as original base64, size and SHA-256 alongside parsed facts. Bounds are256B
canonical,128B boot,8MiB inventory,4096B stat and65536B status; the whole stream
is bounded at8MiB+70KiB. Malformed/truncated/extra frames, component failure,
invalid UTF-8/NUL, malformed/duplicate numeric inventory PIDs, wrong proc PID,
missing birth field or ambiguous proc UID vector are refused. The existing
stream runner waits for process exit and stream completion; nonzero,
cancellation, timeout and limit failures never return an observation or retry.

JSON schema `questionable.file_manager.device_actor_observation.v1` returns
`succeeded`, `result` and `failure`. Successful facts include canonical serial,
boot, every full process row, process-start ticks and all four proc UIDs. The
input serial/PID and raw capture metadata are retained. A failure returns
`result:null`, nonzero CLI status and `state_change_possible:false`; available
bounded partial capture bytes and returned process metadata are retained as
`failure.capture_evidence`. Null returned capture means the stream runner did
not complete, not EOF or known exit. Partial failure bytes are never parsed
into successful facts. Core preserves the original exception type and attaches
this evidence under `exception.Data[typeof(ActorCaptureEvidence)]`.

The caller owns expected canonical/boot, admitted actor UID/PID/birth,
single-process/NAME/ARGS checks, protected-UID absence, source/custody/lease
freshness and before/after effect boundaries. In particular, observations with
a different canonical, additional same-UID process, protected UID or differing
proc UIDs remain visible facts for that caller to reject; File Manager does
not assert that the actor is admitted. This route does not inspect installed
APK bytes, authorize an operation, prove application readiness, render pixels
or grant command/session authority. A current caller must pin its actual
provider and ADB and consume fresh observations at every existing guard wave.

Focused host controls use the real CLI handler/executor and bounded transport
with modeled device output. The fixed device program is also executed through
a real host POSIX shell and CommandRunner with modeled read-only device
utilities, including a middle-read nonzero. That control uses `/bin/sh`, Git's
Windows bash or explicit `QFM_TEST_POSIX_SHELL`. Host controls do not qualify
Android shell availability or performance; actual artifact/current-caller and
device qualification remain separate.
