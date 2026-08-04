# GUI And CLI Operator Parity

Every ADB device operation in the WPF app is represented by one immutable
`OperatorCommand` in the core library. Direct Rusty Kiosk operations use the
immutable `KioskDirectOperatorCommand` and shared executor. In both cases the
WPF button and CLI use the same core method, confirmation semantics, lifecycle
receipt, and readback model. `operator-actions --json` projects the code-owned
registry. Each executable route records its CLI vector, Core factory,
confirmation requirement, and effective-state readback contract. A WPF action
that can use Direct Link or the ADB host provider declares both routes rather
than hiding the fallback. Tests require every WPF click action to map to those
actual routes or one explicit local-interaction reason.
The CLI is intended for agents and automation, so command text is deliberately
not projected into the non-technical WPF interface.

The optional Rusty Fleet subprocess hook is intentionally CLI-only in v1. It
does not correspond to a WPF action: `integration capabilities`, `observe`,
and `invoke` are a strict machine contract, not a hidden GUI route. They remain
disabled by default and cannot invoke push, delete, overwrite, or fan-out.
The dedicated Fleet awake provider is also machine-only, but it is a separate
effect-owner executable rather than a hidden WPF or general CLI route. Fleet's
preview/confirmation UI owns the managed operation; File Manager's ordinary
WPF `Keep awake` button continues to map only to `device keep-awake`.
The distribution-only **Get Fleet** tab is different: its two buttons are
ordinary typed operator routes with exact CLI equivalents. They verify and
handoff one configured Fleet installer release: signed canonical Pages
metadata binds one immutable channel/maturity GitHub Release Setup asset.
Published builds take this trust configuration only from embedded release
metadata; status reports its sanitized source kind. They do not project any
Fleet runtime or device capability.
That tab also owns File Manager's private Quest connectivity-profile
lifecycle. Its status/list/import/revoke controls and the CLI use the same
`OperatorCommand` dispatcher without initializing ADB. The convenience action
that binds the selected USB headset to the already-entered Kiosk direct link
uses the exact CLI standard-input vector and passes the private document to the
shared executor only in memory. It first uses the create-only vector; an
existing record triggers a distinct replacement confirmation before a second
command carrying `--replace-existing`. Its password input and endpoint are
transient and cleared for every outcome.

The Windows release places these programs beside each other:

```text
QuestIonAbleFileManager.exe
questionable-file-manager.exe
```

The CLI can be invoked directly in PowerShell without translating GUI labels
into a different automation model. Agents can include `--adb <path>` when an
exact tool selection is part of the test.

Starting with Labs Alpha.13, CLI `kiosk install` and `kiosk provision` require
exactly one explicit `--product-channel stable|labs`. This is an intentional
fail-closed CLI migration: older scripts that omitted the option receive input
exit code 2 before ADB dispatch. Typed Core callers retain the historical
Stable default.

For app-provided launch options, WPF presents only the bounded read-only rows
returned for Kiosk's current selected app. The CLI equivalent accepts the same
opaque option id, with a 160-character limit. Neither surface can supply an
activity, component, action, URI, flag, path, or arbitrary extra. A completed
request remains pending until Kiosk reads back the exact dispatched option id
together with the exact currently selected package. This confirms dispatch,
not the destination app's foreground state or option semantics. Core also requires the optional
package/UID/signer/version/provider/activity binding fields to be all-or-none
and independently recomputes Kiosk's v1 binding SHA-256 before projection.
Nonblank opaque ids are preserved ordinally, including leading or trailing
whitespace; File Manager does not normalize an app-defined identifier.

## Operation Map

| WPF operation | Equivalent CLI route |
| --- | --- |
| Refresh headsets | `devices` |
| Open, go up, or open a folder | `files list` |
| Pull selected file | `files pull` |
| Push file here | `files push` |
| Refresh packages | `apk list` |
| Export selected package | `apk export` |
| Install on selected headset | `apk install` |
| Install APK bundle | `apk install-bundle` |
| Enable and connect Wi-Fi ADB | `wifi enable` |
| Connect an enabled Wi-Fi headset | `wifi connect` |
| Disconnect selected Wi-Fi headset | `wifi disconnect` |
| Install one APK on checked Wi-Fi headsets | `apk install-many` |
| Install one APK bundle on checked Wi-Fi headsets | `apk install-bundle-many` |
| Refresh optional Kiosk status/catalog | `kiosk status` |
| Install bundled Kiosk pair | `kiosk install --product-channel <stable\|labs> --confirm-kiosk-setup` |
| Provision installed Kiosk helper | `kiosk provision --product-channel <stable\|labs> --confirm-kiosk-setup` |
| Kiosk panel/focus/select/tag/launch/requirement/passthrough/setup action | `kiosk command` |
| Launch one read-only app-provided option | `kiosk command --command launch-option --value <opaque-option-id> --confirm-kiosk-control` |
| Export/import Kiosk tag file | `kiosk tags export` / `kiosk tags import` |
| Connect/refresh Kiosk directly | `kiosk-direct status` |
| Read/cancel one admitted direct request | `kiosk-direct request-status` / `kiosk-direct request-cancel` |
| Direct Kiosk typed action | `kiosk-direct command` |
| Direct tag export/import | `kiosk-direct tags export` / `kiosk-direct tags import` |
| Direct staging list/upload/download/delete | `kiosk-direct files ...` |
| Direct attended APK install/receipt | `kiosk-direct install` / `kiosk-direct install-status` |
| Refresh batteries/power/performance | `device status` |
| Bounded keep awake (one minute through eight hours) / restore normal | `device keep-awake` |
| Set / clear CPU and GPU overrides | `device performance` |
| Check the configured trusted Fleet installer | `fleet status --json` |
| Verify and open Fleet's guided installer | `fleet install --confirm-fleet-install --json` |
| List / check File Manager-owned Fleet connectivity profiles | `connectivity-profile list --json` / `connectivity-profile status --device-id <fleet-device-id> --json` |
| Import one protected connectivity profile file | `connectivity-profile import --file <private-profile.json> --confirm-profile-write [--replace-existing] --json` |
| Save selected USB + entered Kiosk direct link for Fleet | in-memory equivalent of `connectivity-profile import --stdin --confirm-profile-write --replace-existing --json` |
| Revoke selected connectivity profile | `connectivity-profile revoke --device-id <fleet-device-id> --confirm-profile-revoke --json` |
| Optional Fleet capability/observation/list/pull/status | CLI-only `integration ... --json`; no WPF action in v1 |
| Authority-injected Fleet no-overwrite push/cancel | Core API only; absent from the environment-created CLI and WPF |

The WPF **Disconnect** button clears its long-lived process-memory UI session.
It is intentionally marked interactive-only: every CLI direct command is one
atomic session and performs the equivalent cleanup before printing its single
final result, so there is no separate `kiosk-direct disconnect` process route.

Example shapes use placeholders rather than live device or local identities:

```powershell
& '.\questionable-file-manager.exe' files list --serial <quest-serial> --path /sdcard --adb <path-to-adb>
& '.\questionable-file-manager.exe' files pull --serial <quest-serial> --remote /sdcard/Download/example.txt --output <local-path> --adb <path-to-adb>
& '.\questionable-file-manager.exe' files push --serial <quest-serial> --file <local-path> --remote /sdcard/Download/example.txt --adb <path-to-adb>
& '.\questionable-file-manager.exe' apk list --serial <quest-serial> --adb <path-to-adb>
& '.\questionable-file-manager.exe' apk export --serial <quest-serial> --package <package> --output <local-apk> --overwrite --adb <path-to-adb>
& '.\questionable-file-manager.exe' apk install --serial <quest-serial> --file <local-apk> --adb <path-to-adb>
& '.\questionable-file-manager.exe' apk install-bundle --serial <quest-serial> --folder <apk-folder> --adb <path-to-adb>
& '.\questionable-file-manager.exe' wifi enable --serial <usb-serial> --port 5555 --confirm-wifi-adb --adb <path-to-adb>
& '.\questionable-file-manager.exe' wifi connect --host <quest-ip> --port 5555 --confirm-wifi-adb --adb <path-to-adb>
& '.\questionable-file-manager.exe' wifi disconnect --host <quest-ip> --port 5555 --confirm-wifi-adb --adb <path-to-adb>
& '.\questionable-file-manager.exe' apk install-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --file <local-apk> --parallelism 2 --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' apk install-bundle-many --serial <quest-a-ip>:5555 --serial <quest-b-ip>:5555 --folder <apk-folder> --parallelism 2 --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk status --serial <quest-serial> --product-channel labs --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk install --serial <usb-serial> --product-channel labs --confirm-kiosk-setup --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk provision --serial <usb-serial> --product-channel labs --confirm-kiosk-setup --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk command --serial <quest-serial> --product-channel labs --command launch-kiosk --confirm-kiosk-control --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk command --serial <quest-serial> --product-channel labs --command set-launch-requirement --value wifi-on --confirm-kiosk-control --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk command --serial <quest-serial> --product-channel labs --command launch-option --value <opaque-option-id> --confirm-kiosk-control --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk command --serial <quest-serial> --product-channel labs --command passthrough-contour --confirm-kiosk-control --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk tags import --serial <quest-serial> --product-channel labs --file <tag-file> --confirm-kiosk-control --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' kiosk-direct status --serial <usb-serial> --product-channel labs --confirm-kiosk-direct-bootstrap --adb <path-to-adb> --json
& '.\questionable-file-manager.exe' kiosk-direct command --serial <usb-serial> --product-channel labs --confirm-kiosk-direct-bootstrap --command launch-kiosk --confirm-kiosk-control --adb <path-to-adb> --json
& '.\questionable-file-manager.exe' kiosk-direct command --serial <usb-serial> --product-channel labs --confirm-kiosk-direct-bootstrap --command launch-option --value <opaque-option-id> --confirm-kiosk-control --adb <path-to-adb> --json
& '.\questionable-file-manager.exe' kiosk-direct command --serial <usb-serial> --product-channel labs --confirm-kiosk-direct-bootstrap --command cancel-pending-launch --confirm-kiosk-control --adb <path-to-adb> --json
& '.\questionable-file-manager.exe' kiosk-direct files upload --serial <usb-serial> --product-channel labs --confirm-kiosk-direct-bootstrap --file <local-file> --confirm-staging-upload --adb <path-to-adb> --json
& '.\questionable-file-manager.exe' kiosk-direct install --serial <usb-serial> --product-channel labs --confirm-kiosk-direct-bootstrap --file <base-apk> --confirm-local-install --adb <path-to-adb> --json
& '.\questionable-file-manager.exe' device keep-awake --serial <quest-serial> --on --duration-ms 28800000 --confirm-device-settings --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' device performance --serial <quest-serial> --cpu 3 --gpu 3 --confirm-device-settings --json --adb <path-to-adb>
& '.\questionable-file-manager.exe' fleet status --json
& '.\questionable-file-manager.exe' fleet install --confirm-fleet-install --json
& '.\questionable-file-manager.exe' connectivity-profile status --device-id <fleet-device-id> --json
& '.\questionable-file-manager.exe' connectivity-profile list --json
& '.\questionable-file-manager.exe' connectivity-profile import --file <private-profile.json> --confirm-profile-write --json
Get-Content -Raw -LiteralPath <private-profile.json> | & '.\questionable-file-manager.exe' connectivity-profile import --stdin --confirm-profile-write --replace-existing --json
& '.\questionable-file-manager.exe' connectivity-profile revoke --device-id <fleet-device-id> --confirm-profile-revoke --json
```

PowerShell rendering single-quotes paths when required and doubles embedded
single quotes. ADB receives an argument list through `ProcessStartInfo` rather
than a shell command string.

The WPF confirmation dialog is projected as `--confirm-wifi-adb`. The CLI
rejects every Wi-Fi state change when that operator-approval marker is absent.
Agents must not add the flag without approval for the exact target. Parallel
commands repeat `--serial` once per checked headset and return all per-target
results even when the process exit status is nonzero.

The WPF footer's progress bar is a transient projection of the same executor,
not a separate operation. CLI arguments therefore remain identical. Machine-
readable Direct Link output stays one final
`questionable.file_manager.kiosk_direct_cli_result.v1` JSON document after
cleanup on success or failure; JSON failures use fixed sanitized reason codes
and write no plaintext standard error. Agents use typed results rather than
scraping GUI animation or mixed progress lines.

Authorized-USB bootstrap failures retain that same one-document rule. A lost
or malformed enable response is reconciled only with the original operation ID
and no-argument provider status; its typed cleanup receipt is emitted even
though no HTTP client lease was established. Direct installs pass the exact
name, byte count, and lowercase SHA-256 returned by each completed upload, so a
same-name staging replacement cannot silently change PackageInstaller input.
The `kiosk-direct status` route is also the shared adoption projection: on one
client lease it requires signed Direct Link status, a completed typed Kiosk
status whose effective-state readback matches, and signed staging inventory.
WPF publishes a connected session only after that same Core composite succeeds.

Manual direct authentication is the explicit fallback:

```powershell
& '.\questionable-file-manager.exe' kiosk-direct status --endpoint http://<quest-ip>:39873 --credential-stdin --json
```

Type the on-headset credential into standard input and press Enter. Do not put
it in a command, environment variable, transcript, or clipboard. The WPF uses
a `PasswordBox` and offers only a local 15-second reveal. Turning reveal off
remasks the value; reveal timeout, focus loss, deactivation, connection outcome,
disconnect, profile-enrollment outcome, and window close clear both the masked
and revealed projections.

State-changing JSON results wrap the operation payload with a
`mutation` receipt. Its ordered transitions are `sent`, `pending`, and only
then `confirmed` when route-specific headset readback matches. A prompt-gated
request may finish with `pending`; this is a successful request admission, not
a claim that the wearer accepted it. The WPF status line uses the same receipt.

## Acceptance

`OperatorCommandTests` must cover the exact CLI argument vector for every WPF
operation, PowerShell quoting, and execution through the shared dispatcher into
serial-scoped ADB calls. Bundle validation additionally proves that every
top-level APK is sent in one deterministic `install-multiple` call. Live
validation then runs the same CLI routes against one explicitly selected
authorized headset; raw serials, package names, APKs, and evidence remain local
and ignored.

Wi-Fi and parallel acceptance additionally proves address inspection occurs
before `tcpip`, no daemon lifecycle command is emitted, each install remains
serial-scoped, concurrency is bounded, duplicate targets are rejected, and
partial failures remain visible.

Fleet installer parity additionally proves the CLI accepts only the two exact
argument vectors above, install requires the same explicit confirmation as the
WPF dialog, and neither route initializes ADB. Offline contract tests cover
strict/duplicate JSON, signatures and signer pins, product/channel/asset
binding, v1 rejection, exact immutable URL/version tag, size and digest,
JCS byte canonicalization, exact 24-hour/future-skew/expiry boundaries,
replay/downgrade and fail-closed state/anchor deletion, Pages-binary rejection,
redirect escape, embedded configuration precedence/completeness, generated
trust absence from ordinary build inputs, checked-in release-source authority,
protected machine-record lifecycle loss, same-user replay-file rewrite,
nonempty protected-state reconstruction of missing local files, refusal to
reconstruct missing machine authority from local evidence, explicit-only
destructive reset, synthetic same-signer helper A-to-B replacement with
retained identity and state preservation, verified rollback readback,
adversarial rollback-destination substitution with validated-backup retention,
error-1177-shaped partial replacement with bounded reconciliation and prior
backup retention, rollback-specific error-1177 missing/moved/changed states
with candidate and backup evidence preservation, different-signer rejection,
visible guided-process retry
semantics, receipt redaction, process timeouts, private-stage cleanup, and
reparse rejection.

Connectivity-profile parity proves status/list receipts contain only IDs and
sanitized state, both import sources reach the same strict parser and
Credential Manager writer, secrets never enter CLI arguments or receipts,
write/replacement/revocation confirmations fail closed, and WPF convenience
enrollment uses the same standard-input command without a temporary file.
Mock-store tests cover create, replace, invalid stored state, revoke, duplicate
and unknown JSON fields, target binding, input ambiguity, size bounds,
post-write create cleanup, prior-record restoration, rollback failure, and
owned-buffer zeroization.

Direct-link acceptance uses shared Kotlin/C# HMAC vectors, rejects response-ID,
digest, and signature mismatches, and keeps Android install receipts pending
until the matching session reports installed or failed. It does not initialize
ADB and has no fleet or fan-out route.

Fleet interop acceptance is separate: it verifies exactly one final JSON
document, explicit non-ready states, strict schemas, exact-serial rediscovery,
read-only operation admission, remote canonical descriptor binding, a
mid-stream byte ceiling, locked local ancestor/final-file identity, coherent
digest evidence, and cancellation/timeout cleanup. Adding a WPF control later
would require a separately reviewed
operator workflow and a parity route; this CLI-only adapter does not silently
appear in the current app.
