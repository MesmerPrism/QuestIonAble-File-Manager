# Extended fixed actor identity observation

The agent-only read-only route is separate from unchanged `actor-observe`:

```text
questionable-file-manager device actor-identity-observe --serial <exact-serial> --pid <positive-decimal> --json
```

The immutable `OperatorCommands.ObserveDeviceActorIdentity` command invokes
`AdbClient.ObserveDeviceActorIdentityAsync`. No WPF, local API or dedicated
Fleet provider route is added. Only exact serial and positive int32 PID are
accepted; no package, path, shell program, discovery, launch, wake, mutation,
lease, retry or daemon choice is exposed.

One serial-scoped bounded streaming invocation runs this fixed sequence:
canonical serial, boot, complete numeric process inventory, initial proc stat,
raw proc cmdline, complete `pm list packages -U` registry, proc status, final
proc stat. Only the validated decimal PID enters fixed proc paths. Reads are
sequential, not an atomic snapshot. Every read failure stops the group.

`QFM_ACTOR_IDENTITY_V1` frames exactly eight ordered sections named `serial`,
`boot`, `inventory`, `initial_stat`, `cmdline`, `package_uids`, `status`,
`final_stat`: `name:exit-code:byte-count` plus exact payload bytes, LF separator,
then `END` and EOF. Their bounds are 256B, 128B, 8MiB, 4096B, 65536B, 8MiB,
65536B and 4096B; total framing overhead is bounded by another 1024B.

Cmdline is opaque binary, including NUL, non-UTF8 and an empty read. The fixed
program reads that proc file once through `base64` into a NUL-safe shell value,
then decodes the same bytes for count and raw frame payload. There is no remote
temporary file or second proc read. Decode failure, count inconsistency,
truncation or missing tooling fails closed. All other sections retain strict
UTF-8 and NUL refusal. Original `actor-observe` framing and parser are unchanged.

JSON schema `questionable.file_manager.device_actor_identity_observation.v1`
has one final `succeeded/result/failure` envelope. The typed result includes
all process rows, both proc birth values, the exact binary `CommandLine` raw
section, all package/UID rows and four process UIDs. `RawSections` retains
each original payload's byte count, SHA-256 and base64. Malformed or duplicate
package names/process PIDs, malformed proc fields, framing/order/bounds errors,
nonzero, stderr, cancellation, timeout and overflow never produce facts or retry.
Both birth values and shared/different UIDs remain facts; equality, package
ownership, truncation interpretation and admission are caller policy.

Failure keeps `result:null`, a nonzero CLI status and
`state_change_possible:false`. Bounded available raw prefix and returned native
metadata are retained as `failure.capture_evidence`, with original Core exception
type preserved. Null returned metadata is not EOF/exit evidence. ADB exec-out
can return host zero for a failed remote read; incomplete/failed framing still
refuses. Text read failures retain preceding successful sections and the failed
header, not the failed command's stdout. A failed binary read/decode can leave
an incomplete raw prefix, which remains failure evidence only.

The caller still owns unknown-PID discovery, exact package-to-exclusive-UID
registry checks, command identity, expected initial/final birth, protected UID
absence, before/after effect boundaries and live source/actor/lease/expiry
guards. This route does not assert actor admission, installed APK equality,
application readiness, cleanup or wearer visibility. No existing consumer is
automatically switched to it. Host controls exercise real parser/CLI/executor,
bounded streams and the exact program with modeled device utilities in a host
POSIX shell; Android tooling and runtime qualification remain separate.
