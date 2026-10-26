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

The versioned `/v1` and `/v2` surfaces contain capabilities, preflight, execute,
status, and cancellation endpoints. Bodies are bounded to 16 KiB and parsed with exact
case-sensitive fields; unknown and missing fields reject. Private local APK
paths and exact serials exist only in authenticated local request/results and
are deliberately omitted from public examples and schemas. Responses replace
the private state-root pathname with an operation-scoped `retained://`
artifact identifier, including nested command and result projections.

The unchanged v1 closed allowlist is `apk.inspect`, `apk.install-inspected`,
`app.launch-resolved`, and `runtime.observe`. V2 initially admits only
`apk.deploy`: one exact inspected base APK, one ready serial, and the existing
Core install/launch/runtime-observation command. Neither version admits split
sets, batches, diagnostic output reservations, properties, or caller-selected
shell operations.

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
preflight and include physical orphan files already present in staging. One
retained artifact is bounded to 1 GiB and all retained staged artifacts are
bounded to 2 GiB. A per-artifact or aggregate rejection reports only the
requested, available, and configured-limit byte counts; it does not project the
state root or an artifact path.
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


## V2 exact single-target deploy

The standalone native host adds these authenticated routes on its existing
explicit listener; it uses the same registry, state root, integrity key,
journal/anchor, exclusive state owner, staging custody, and capacity limits.
Starting a second owner or initializing separate v2 state is unnecessary.
The browser host projects this same owner through its explicitly versioned
retained routes and reviewed deploy control. See [browser operations](browser-operator.md);
other advanced deployment policies remain outside this slice.

| Route | Core registry entry point | Request |
| --- | --- | --- |
| `GET /v2/capabilities` | `GetV2Capabilities` | No body |
| `POST /v2/preflight` | `PreflightV2Async` | `LocalApiV2PreflightRequest` |
| `POST /v2/execute` | `ExecuteV2Async` | `LocalApiV2ExecuteRequest` |
| `POST /v2/status` | `GetV2Status` | `LocalApiV2OperationRequest` |
| `POST /v2/cancel` | `CancelV2` | `LocalApiV2OperationRequest` |

Every v2 body requires
`contractVersion: "questionable.file_manager.local_api.v2"`. Preflight requires
`command: "apk.deploy"`, `apkPath`, and `serial`; its only optional fields are
`installOptions` and `expiresInSeconds`. Install options retain the v1 shape:
`replaceExisting` defaults true, `grantRuntimePermissions` defaults false,
and both `allowDowngrade` and `allowTestPackages` must remain false. Unknown
fields, duplicate properties, missing required values, other actions, and
unsupported options reject before admission. No verification-policy selector
or development-metadata fallback is admitted.

Execute requires only `operationId` and `commandDigest` in addition to the
contract version. Status and cancel require only `operationId` and the version.
The retained operation's version must match the selected entry point: v1 calls
cannot execute, expire, or cancel v2 work, and v2 calls cannot operate v1 work.
A version mismatch rejects before dispatch, expiry, cancellation, or journal
persistence. Responses reuse `LocalApiPreflightResult` and
`LocalApiOperationStatus`, reporting the operation's actual contract version
and the same `retained://` path redaction as v1.

V2 has a separate SHA-256 digest domain. Its canonical UTF-8 JSON binds the v2
contract, `apk.deploy` action, `exact-installed-install-launch.v1` policy,
exact target serial, normalized immutable install options and CLI arguments,
and one ordered APK resource with role, staged filename, size, hash and
package/version/signer identity. The existing v1 digest algorithm and v1
request DTOs are unchanged. Execute never accepts replacement arguments or
rebuilds the command from caller input.

Journal rows for v1 remain untagged with their original field names and shape.
A v2 row adds `operationVersion` with the full v2 contract value. Mixed v1/v2
journals reopen under the same authenticated envelope and monotonic anchor;
every retained policy and digest is checked before orphan cleanup, and every
remaining staged APK is rehashed and re-inspected before acceptance. Unknown
operation versions reject before artifact cleanup. Older binaries reject the
new field through strict unknown-member parsing before artifact cleanup;
returning to an older binary requires preserving that newer state, never
removing the tag or rewriting retained evidence to appear legacy.

Admission consumes once and execution remains owned until Core returns a
terminal acknowledgement. HTTP disconnect, page loss, or cancellation of the
caller's `ExecuteV2Async` token does not cancel admitted execution. Only the
explicit cancel route requests operation-token cancellation. It reports
`cancellationRequested` while the executor has not acknowledged; after possible
mutation dispatch, cancellation without conclusive readback remains
`outcomeUnknownRecoveryRequired`. V1 pre-execution cancel behavior remains:
a preflighted operation rejects cancellation and otherwise expires naturally.

A completed deploy confirms only the existing Core claim: exact installed
bytes and the resolved launch effect. Install success without confirmed launch
readback remains unknown/pending, and application/OpenXR readiness remains
unknown. Restart never replays consumed operations; interrupted mutations
become unknown. Terminal and unknown records retain the existing bounded
24-hour default lifetime and journal-before-delete cleanup-debt behavior.
