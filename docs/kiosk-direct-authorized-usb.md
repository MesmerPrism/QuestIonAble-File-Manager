# Authorized-USB Kiosk Direct Link

QuestIonAble File Manager can bootstrap Rusty Kiosk's authenticated local
operator link from an already-authorized classic USB ADB transport. This is not
Wi-Fi ADB and it does not restart, reconnect, or otherwise own the shared ADB
daemon.

## Product And Transport Binding

The operator selects one exact USB serial and one product channel. File Manager
uses only these code-owned contracts:

| Channel | Package | Provider authority |
| --- | --- | --- |
| Stable | `io.github.mesmerprism.rustykiosk` | `io.github.mesmerprism.rustykiosk.operator` |
| Labs | `io.github.mesmerprism.rustykiosk.labs` | `io.github.mesmerprism.rustykiosk.labs.operator` |

Before requesting a session, Core requires exactly one ready non-network ADB
transport for that serial, reads the exact installed package UID, and verifies
host schema `rusty.kiosk.host_operator.v4` plus matching package/channel. The
provider issues schema `rusty.kiosk.direct_usb_bootstrap.v2`. The shared wire
fixture is
`references/rusty-kiosk-direct-usb-bootstrap-contract.v2.json`.
Bootstrap operation IDs live in a fixed, non-evicting 4,096-entry ledger scoped
to Kiosk's app-private bootstrap-issuance epoch. Saturation, malformed state, or
epoch mismatch fails closed; bridge-generation changes never clear replay IDs.
Stored replay arrays initialize only with a wholly fresh
`rusty.kiosk.operator_session_state.v1` document; a present missing, null, or
wrong-type array is damaged state and fails closed.

## Credential Lifetime And Cleanup

The bootstrap response is captured as bounded bytes. Only the parser sees those
bytes; output buffers are zeroed, and no ordinary `CommandResult` can expose the
session secret. The HTTP client owns the decoded key only in process memory and
zeros it on close. Session identity and generation are checked again through
authenticated `/v1/status` before any operator action is accepted.
Connection adoption is one shared Core composite used by WPF and CLI status: it
also requires a completed typed Kiosk status with matching effective-state
readback and a signed staging inventory before the client is published.

If bootstrap enabled the listener, cleanup sends `direct-disable` with the
originating operation ID, session ID, and exact pre-disable generation. Disable
is asynchronous. File Manager takes the post-disable generation from the
accepted response and polls no-argument `direct-status` until both
`direct_enabled=false` and `direct_running=false` on that same generation.
Generation drift, transport failure, or bounded non-convergence produces a
sanitized `cleanup_unknown` receipt. A listener that was enabled before this
request is preserved.

If the sensitive `direct-enable` response is lost or malformed, File Manager
does not retry enable and cannot rely on a session ID or secret. It invokes
`direct-recover-disable` with only the original operation ID, then reconciles
no-argument status to disabled and stopped. Kiosk's bounded non-secret ownership
tombstone makes a lost STOP response idempotently recoverable; failure to prove
the stopped result remains `cleanup_unknown`.

Each uploaded APK returns an exact `{name,bytes,sha256}` commitment. Install
submits those commitments, not filenames alone. Kiosk rechecks the staged
identity and counts and hashes bytes from the same opened handle used for the
PackageInstaller copy, failing and abandoning the session if replacement is
detected.
If Kiosk cannot confirm abandonment, it returns incomplete `cleanup-required`.
File Manager retries the identical install body once with a fresh authenticated
transport request ID; Kiosk may retry cleanup only and cannot start a second
install for that logical install request ID. Only returned abandonment or
confirmed session absence becomes terminal `failed`.
Cleanup retry is bound to exact ordered `{name,bytes,sha256}` commitments and
their canonical digest. Kiosk's private `rusty.kiosk.local_install_state.v2`
distinguishes absent, valid, and damaged receipts; damaged state cannot admit a
new session, and the private binding is not exported in public receipts.

Each CLI invocation is one atomic session: cleanup completes before its single
final JSON document is written. Success and failure both use
`questionable.file_manager.kiosk_direct_cli_result.v1`; a JSON failure uses a
fixed reason/message and never falls through to plaintext standard error. The
WPF keeps a session only while its window is connected and reports cleanup
separately on explicit disconnect. Window close also clears it.
If a required WPF adoption readback fails after USB bootstrap, Core closes the
owned session first and throws one sanitized combined failure containing both a
fixed readback reason and the typed cleanup stage. `cleanup_unknown` is never
hidden by rethrowing the earlier readback exception.

## CLI

Preferred authorized-USB form:

```powershell
& '.\questionable-file-manager.exe' kiosk-direct status `
  --serial <usb-serial> `
  --product-channel labs `
  --confirm-kiosk-direct-bootstrap `
  --adb <path-to-adb> `
  --json
```

The manual fallback reads one bounded line from standard input:

```powershell
& '.\questionable-file-manager.exe' kiosk-direct status `
  --endpoint http://<quest-ip>:39873 `
  --credential-stdin `
  --json
```

Type the credential and press Enter. Do not place it in an argument,
environment variable, transcript, or clipboard. WPF uses a masked `PasswordBox`;
its optional reveal is local and lasts at most 15 seconds. Turning reveal off
remasks the retained input; the reveal timeout, focus loss, window deactivation,
connect outcome, explicit disconnect, profile-enrollment outcome, and window
close clear both the masked input and reveal projection.

`request-status --request-id <id>` reads one admitted lifecycle without
enqueuing another action. `request-cancel --request-id <id>
--confirm-kiosk-control` can cancel only that still-pending request. Upload,
delete, tag import, Kiosk mutation, bootstrap, and attended install retain their
dedicated confirmation flags.

## Test Before Publication

Most acceptance is source-level and can run against local fakes before any Labs
publication:

1. Release-build Core, CLI, and WPF.
2. Run the Core suite, including Stable/Labs substitution, multi-device exact
   serial rejection, secret-buffer zeroing, HMAC/session/generation checks,
   lifecycle identity, staging bounds, WPF/CLI action registry, and async
   cleanup convergence.
3. Compare the checked-in bootstrap fixture with Rusty Kiosk's copy.
4. Run CLI help and public-boundary scans; verify the help exposes only
   `--credential-stdin` and authorized-USB authentication.

A live candidate APK is needed only for cross-repository/device validation. An
agent with exclusive use of the selected Quest can run the exact CLI bootstrap,
status, request lifecycle, tags, staging, and cleanup checks without Windows UI
automation. A human wearer remains genuinely required for Meta's USB trust
prompt when the PC key is not already trusted, enabling Kiosk's unknown-app
installer permission, accepting/cancelling Android PackageInstaller sessions,
and clearing Guardian or other system surfaces. Those wearer-owned prompts are
never automated or inferred from request admission.
