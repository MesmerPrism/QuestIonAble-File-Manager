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
host schema `rusty.kiosk.host_operator.v3` plus matching package/channel. The
provider issues schema `rusty.kiosk.direct_usb_bootstrap.v1`. The shared wire
fixture is
`references/rusty-kiosk-direct-usb-bootstrap-contract.v1.json`.

## Credential Lifetime And Cleanup

The bootstrap response is captured as bounded bytes. Only the parser sees those
bytes; output buffers are zeroed, and no ordinary `CommandResult` can expose the
session secret. The HTTP client owns the decoded key only in process memory and
zeros it on close. Session identity and generation are checked again through
authenticated `/v1/status` before any operator action is accepted.

If bootstrap enabled the listener, cleanup sends `direct-disable` with the
originating operation ID, session ID, and exact pre-disable generation. Disable
is asynchronous. File Manager takes the post-disable generation from the
accepted response and polls no-argument `direct-status` until both
`direct_enabled=false` and `direct_running=false` on that same generation.
Generation drift, transport failure, or bounded non-convergence produces a
sanitized `cleanup_unknown` receipt. A listener that was enabled before this
request is preserved.

Each CLI invocation is one atomic session: cleanup completes before its single
final JSON document is written. The WPF keeps a session only while its window is
connected and reports cleanup separately on explicit disconnect. Window close
also clears it.

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
its optional reveal is local, lasts at most 15 seconds, and remasks on focus loss
or window deactivation. Connect outcome, disconnect, and window close clear the
input.

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
