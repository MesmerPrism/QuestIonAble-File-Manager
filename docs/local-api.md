# Dedicated local API

`questionable-file-manager-api` is an optional Windows-only,
inert-until-started executable.
It directly hosts the Core typed-command registry; it is not WPF behavior and
does not wrap or spawn the general CLI.

Startup requires `--listen` with one explicit numeric HTTP loopback address and
port. Wildcard, hostname, non-loopback, HTTPS, missing, and auto-discovered
addresses reject. The process never starts a listener unless explicitly run.

Authentication uses a bearer credential read only from
`QUESTIONABLE_FILE_MANAGER_API_BEARER`. It must be 32 through 512 UTF-8 bytes,
is never accepted on the command line or in a body, is not returned or logged,
and is compared using a fixed-time byte comparison.

Private durable state is explicitly configured through
`QUESTIONABLE_FILE_MANAGER_API_STATE`; no default public path is baked in.
`QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET` supplies 32 through 512 private
UTF-8 bytes from which the fixed-size journal integrity key is derived. The
state directory must be on a local non-UNC volume. Every existing ancestor,
the state root, and staging directory is opened without following reparse
points and retained by identity-bound handles. A newly created root receives a
protected current-user/SYSTEM/Administrators ACL; an existing root fails
closed unless its ACL is protected and every writable allow rule belongs
exactly to the current user, SYSTEM, or Administrators. One exclusive
state-owner lease prevents multiple API processes from writing the same
journal or staging inventory.

The public registry constructor requires explicit state settings. The
zero-key, generated-path convenience constructor is internal and exposed only
to the Core test assembly; production callers cannot silently enter test mode.

The versioned `/v1` surface contains capabilities, preflight, execute, status,
and cancellation endpoints. Bodies are bounded to 16 KiB and parsed with exact
case-sensitive fields; unknown and missing fields reject. Private local APK
paths and exact serials exist only in authenticated local request/results and
are deliberately omitted from public examples and schemas. Responses replace
the private state-root pathname with an operation-scoped `retained://`
artifact identifier, including nested command and result projections.

The initial closed allowlist is `apk.inspect`, `apk.install-inspected`,
`app.launch-resolved`, and `runtime.observe`.

Preflight copies an admitted source into private state with create-new,
no-overwrite semantics, rejects source/state reparse points and hard links,
and retains a non-delete-sharing read handle. Hashing, Android Build Tools,
preflight, execute, and ADB install use only those staged bytes. It constructs
and retains the exact immutable `OperatorCommand`, checks the exact ready target
for device routes, binds staged artifact facts into the command digest, and
returns a short-lived operation identifier. Execute supplies only that
identifier and digest, atomically consumes the retained command once, and never
rebuilds a command from execute-time arguments.

An integrity-protected bounded journal records command/digest, expiry, consume
and dispatch markers, legal state, staged binding, mutation evidence, and
replay tombstones. Its monotonic envelope/hash chain is checked against a
separately protected anchor, so replacing only the journal with an older valid
copy fails closed. Journal and anchor files are single-link, non-reparse,
handle-validated files. On restart every staged APK is reopened by its retained
identity contract, rehashed, and re-inspected before its journal record is
accepted. This detects accidental or adversarial stale-file substitution by a
trusted local operator, but it does not claim rollback resistance after full
compromise of the same Windows user who controls both private state and its
secret.

Restart never re-executes consumed work. Interrupted
read-only work fails; an interrupted dispatched mutation becomes
`outcomeUnknownRecoveryRequired` for typed readback reconciliation.

Operation/file/byte reservations are serialized across the full concurrent
preflight and include physical orphan files already present in staging.
Terminal pruning first durably records cleanup debt, then closes the immutable
read handle and deletes only the same identity through a delete-only handle.
Failed deletion remains explicit and is retried after restart; a missing
cleanup-debt artifact is treated as a completed prior deletion and its
tombstone is cleared. Journal recovery temporaries and all retained
errors/results are bounded. Installed-base readback is a bounded binary stream
to a hash sink, so it creates no crash-residue APK directories on the host.
On startup, operation-owned `*.apk` files absent from the authenticated journal
are treated as crash-window orphans and removed by identity-bound handle before
new admission, preventing permanent physical-capacity exhaustion.

Status returns bounded structured `OperatorExecutionResult` and mutation
evidence. A pending mutation receipt never maps to API `completed`.
Cancellation first becomes `cancellationRequested`; `cancelled` is terminal
only after executor acknowledgement. Cancellation after possible mutation
dispatch without conclusive readback becomes
`outcomeUnknownRecoveryRequired`. Cancellation affects only that operation's
token and does not stop unrelated processes, devices, or ADB state.

There is no raw shell/ADB surface, arbitrary component or intent input,
property/process control, credential body field, Fleet/Manifold/Agent Board
authority input, endpoint discovery, or general filesystem route.
Inspected install through this API rejects downgrade and Android `testOnly`
requests; those explicit development options remain CLI-only.
