# Optional browser operator host

`questionable-file-manager-browser` is a separately started Windows, Linux or macOS host for a
closed browser projection of existing QFM Core commands. Ordinary host ADB
operations require no headset application. Developer mode, protected debugging
authorization, a suitable USB/network transport and a selected host ADB remain
prerequisites. Its retained APK projection calls the existing inspected-deployment
owner in-process, preserving that owner's registry, journal and deployment
security boundary without opening a second API listener.

## Start explicitly

Configure `QUESTIONABLE_FILE_MANAGER_BROWSER_BEARER` privately in the process
environment with an unpredictable bearer credential of 32–512 UTF-8 bytes.
Never place it in a URL, command argument, committed file, shell history or log.
The interface accepts it into tab memory and supplies the Authorization header.
The host does not supply a default credential or open a browser automatically.
Use QFM's existing `QUESTIONABLE_FILE_MANAGER_ADB` setting to select the host
ADB executable without changing global ADB state.

```powershell
dotnet run --project src/QuestIonAbleFileManager.BrowserHost -- --listen http://127.0.0.1:8127/
```

Open that exact origin. The listener requires an explicit numeric HTTP loopback
address and port; IPv4 and IPv6 loopback are supported. The request Host must
match its configured authority, any Origin must match its exact origin, the
remote peer must be loopback, and cross-site fetch metadata is rejected.
No API exception exists for public origins. A fixed allowlisted static GET may
open from a public link or browser extension only as a document navigation
(`Sec-Fetch-Mode:navigate`, `Sec-Fetch-Dest:document`); exact Host and loopback
peer checks still apply. This inert page navigation performs no discovery or
API operation. Cross-site API requests and subresource fetches remain denied.
No CORS origins, wildcard listeners, cookies or credential-bearing query strings
are accepted. Public distribution pages can link to a local interface; they
cannot invoke this API cross-origin. Static assets are a fixed allowlist under
the executable's `wwwroot`; there is no directory browsing or arbitrary file
route. API responses disable caching. The PWA worker stores only a fixed public shell/help asset set and bypasses all APIs, authenticated requests, non-GET requests and query strings. Responses set a self-only CSP. Logs are disabled.

Starting the process or loading a page performs no ADB discovery or mutation.
ADB is resolved only on an explicit operator action. This development output
is framework-dependent: retain its sibling assemblies, runtime metadata and
wwwroot; it is not one of the dedicated Fleet provider trust units. No signed
release or installed Windows-service behavior is claimed.

## Installable shell and offline help

Open the exact local host address, then use **Install QFM** when the browser
offers it, or its install/Add to Home Screen menu. Installation support varies
by browser and OS. Keep that origin and port stable: changing the port creates
another browser app and USB permission origin. The connector still needs a
compatible .NET runtime and host tools; installing the PWA does not install ADB.

After the first completed shell download, QFM's interface and agent guide can
open with the connector stopped. Only fixed shipped public UI/help assets enter
the service-worker cache. API results, tokens, logs, captures, APKs and device
files never do. There is no background sync or mutation replay. A network change
disconnects the host projection and clears its token; it retains an unresolved
original request for reconciliation. Reconnecting never executes or reconciles
anything automatically. An offline network indication is advisory: an explicit
connection to a running local connector can still support USB ADB.

The connector remains loopback-only. On a phone, localhost means the phone,
not a PC attached to a Quest. Use the connector on the attached computer or a
supported direct USB browser; remote-PC access is not currently supported.

The authenticated session reports host implementation capabilities without
discovering devices or verifying tool installation. Ordinary ADB-backed host
routes are available on Windows, Linux and macOS; Linux has a fake-ADB execution
smoke check, while macOS hardware/runtime validation remains outstanding.
Windows Credential Manager profiles, the signed Fleet guided installer and
the separate protected Local API state remain Windows facilities. Other hosts
report those limits explicitly; they do not substitute a plaintext secret store.

Browser parity is still in progress. See the complete
[feature/platform migration inventory](browser-pwa-migration.md). The CLI and
WPF frontend remain available while browser workflows and distribution mature.

## Typed API

All API routes require `Authorization: Bearer <private-credential>`.

| Method/path | Contract |
| --- | --- |
| `GET /browser/v1/session` | Version, current process session nonce, closed actions and capacity. No device query. |
| `POST /browser/v1/execute` | One strict JSON command. Additionally requires `X-Qfm-Session` from the current session response. |
| `GET /browser/v1/requests/{requestId}` | Original retained running/result state; also requires the session header. Never reruns an action. |

Windows hosts additionally report `artifactStaging` platform support and
advertise `apk.inspect`. Support is not root admission: the first explicit
staging request must still create a private, local NTFS directory and retain
its native file/ancestor handles. Other platforms return a fixed unavailable
reason and the Windows companion alternative. They retain portable device
read/control routes.

Execute Content-Type is exactly `application/json`. Bodies are capped at
4 KiB, JSON depth is capped, fields are case-sensitive, duplicate/unknown
fields reject, and each action requires exactly its listed fields:

| Action | Fields beyond `requestId` (nonempty GUID) and `action` |
| --- | --- |
| `devices` | none |
| `device.status` | `serial` |
| `wifi.enable` | `serial`, `port`, `confirmed:true` |
| `wifi.connect` | `serial` equal to `host:port`, `host`, `port`, `confirmed:true` |
| `device.keep-awake` | `serial`, `durationMilliseconds`, `confirmed:true` |
| `device.normal` | `serial`, `confirmed:true` |
| `wifi.disconnect` | `serial` equal to `host:port`, `host`, `port`, `confirmed:true` |
| `files.list` | `serial`, `path` (absolute headset folder) |
| `apk.list` | `serial` |
| `apk.permissions` | `serial`, `package` (exact package name) |
| `device.performance` | `serial`, `cpu`, `gpu`, `confirmed:true` |
| `device.performance-clear` | `serial`, `confirmed:true` |
| `device.actor-observe` | `serial`, `pid` (positive process ID) |
| `device.actor-identity` | `serial`, `pid` (positive process ID) |
| `adb.forwards` | `serial` |
| `apk.inspect` | `artifactId`, `sha256`, `sizeBytes` (from a completed upload receipt) |

Port is an integer 1–65535. Hold duration is an integer 60000–28800000
milliseconds. CPU/GPU values are integers 0–5 or null to preserve that override;
at least one level must be supplied. Clear removes both overrides. No
caller-selected shell, ADB executable, host file path, property, Fleet identity,
watchdog or command argument is accepted. Package, process ID and headset path
inputs reach only their closed read-only Core handlers.
Serial-scoped operations first use the same Core discovery command to require
one exact ready serial. Wi-Fi enable additionally retains Core's USB-to-network
stable-identity join. Connect intentionally does not require the disconnected
endpoint to be ready before connection; Core verifies that exact endpoint
afterwards. Disconnect permits an already disconnected endpoint and uses Core's
fresh absent-endpoint readback. Its `serial` input is endpoint confirmation, never a claim that
the endpoint matches a canonical headset identity or boot.

Example shape (placeholder values must be replaced by the actual approved
target and a fresh GUID):

```json
{
  "requestId": "00000000-0000-4000-8000-000000000001",
  "action": "device.keep-awake",
  "serial": "<exact-ready-target>",
  "durationMilliseconds": 60000,
  "confirmed": true
}
```

Commands invoke the same immutable `OperatorCommand` and
`OperatorCommandExecutor` as CLI and WPF. Success returns
`{requestId,action,state,result,error,mutation}` with camelCase Core results,
CLI-equivalent command arguments and the original `mutationReceipt` when Core
produces one. Its enum property is `stage`, not `state`. A headset mutation
is `completed` only when that receipt is `confirmed`; incomplete readback is
`pending`. Core classifies Wi-Fi connect as a host transport effect and does
not create a headset mutation receipt. It is `completed` only when its typed
`wifiAdbConnectionResult` proves the exact endpoint ready. That proves no boot,
app readiness or canonical identity. API/ADB exit success alone is insufficient.

The host admits only one concurrent operation, retains at most 256 request
records, and never evicts a record to permit replay. An admitted request ID is
consumed even if the browser disconnects, readback is pending, the operation
fails or the outcome is unknown. Duplicate execute returns 409. Use GET on
the original ID and a separate explicit status action to inspect state; never
automatically retry a pending/unknown mutation under a new ID. Known admission
rejections can return `dispatched:false`; this describes only that rejected
request, not a previous operation. Exhausted capacity requires a deliberately
new host session after outstanding uncertainty has been handled.
`request_already_consumed` never returns `dispatched:false`: its original
record may still be running or may have changed the target. A duplicate of a
running ID is classified as consumed before the busy check.

Body reads have a five-second deadline and admitted commands have a 90-second
cancellation deadline through Core's bounded runner. Closing the page does not
cancel an admitted effect. In-memory records do not survive process restart;
the new process generates a different session nonce and rejects old session
headers, even if the private bearer was reused. This is session-scoped replay
protection, not durable crash recovery. Preserve unresolved original operation
identity and inspect current state before any explicit recovery.

## Protected transient artifact transport

All artifact routes require the same exact origin, bearer and current session
header. They never accept a host filesystem path. Staging is currently Windows
NTFS only, with a private ACL, a retained owner marker, ancestor/file identity
checks, immutable leases and nonrecursive owned-file cleanup. This does not
replace the separate Local API's durable operation authority.

| Route | Input and result |
| --- | --- |
| `POST /browser/v1/artifacts` | Raw `application/octet-stream`; `X-Qfm-File-Name`; optional `X-Qfm-Artifact-Sha256`. Returns HTTP 201 and `{state:"staged",scope:"localOnly",artifact:{id,fileName,sizeBytes,sha256},deviceDispatched:false}`. |
| `GET /browser/v1/artifacts/{id}` | `X-Qfm-Artifact-Sha256` and `X-Qfm-Artifact-Size-Bytes` must match the original receipt. Returns only those verified bytes as an attachment. |
| `DELETE /browser/v1/artifacts/{id}` | Removes one owned unleased staged file. It never removes the original user file. |
| `POST /browser/v1/artifacts/cleanup` | Empty body; explicitly clears owned unleased staged files. Active custody and foreign files remain retained. |

The upload endpoint alone widens the command body's 4 KiB limit. Upload and
download have a 90-second deadline and enforce 256 MiB per file, 8 retained files
and 512 MiB total per process. A file name is one bounded simple basename; no
slashes, traversal, device names or alternate streams. The UI offers an editable
staged name for source names outside that contract.

`apk.inspect` uses the same immutable Core factory and inspector as the CLI,
bound to that artifact's exact ID, size and lowercase SHA-256. It consumes a
normal one-use request ID. Its response uses `artifactInspection` with the
artifact receipt and Core inspection facts; Core commands and private staging
paths stay inside the connector. The inspection's `path` is only the staged
display filename. A mismatch fails without publishing inspection success.

The shell never caches these routes. Download additionally verifies the final
size and SHA-256 in the browser. A lost upload response never retries; explicitly
clear staged files after reconnecting if local capacity needs recovery. The UI
keeps staged references in tab memory and rejects them when the host session
changes. Process restart invalidates IDs; it does not establish durable crash
recovery or authorize scavenging unrelated files.

Transient staging performs local transport and inspection. The separate retained
workflow below owns inspected single-APK install, launch and runtime observation.
Device file push and the remaining advanced deployment options still use the
existing typed CLI/WPF during migration.

## Retained inspected APK operations

On Windows, configure `QUESTIONABLE_FILE_MANAGER_API_STATE` and
`QUESTIONABLE_FILE_MANAGER_API_JOURNAL_SECRET` privately using the existing
[Local API state contract](local-api.md). Preserve that exact state and its
32–512 UTF-8 byte integrity key across sessions and process restarts. The browser
bearer is a separate credential. This projection needs no standalone Local API
bearer or listener. Configuration reporting is inert; an explicit retained
request opens and validates the owner. If another process owns the same state,
use that existing owner or stop it through its normal lifecycle. Do not choose
a fresh directory or replace the key to bypass outstanding uncertainty.
Linux/macOS report this protected retained owner unavailable and offer the
Windows companion alternative.

Choose and stage an APK, select one ready target, then prepare an inspected
install, resolved launch or runtime observation. Preparation creates the owner's
independent protected copy and commits its journal before releasing the transient
upload lease. Review package, version, signer, SHA-256, size, target, options and
expiry before approving and executing once. Removing the transient upload does
not remove this retained copy. A present different APK selection blocks approval;
after authentication or source removal, explicitly read the original status
again. Default approval lifetime is 120 seconds, with a maximum of 300 seconds.
A prepared, unexecuted owner record cannot be cancelled by v1; let its approval
expire and explicitly read status before replacing that browser reference.
Single-APK install supports replace-existing and requested runtime permission
options. Downgrade, test-only, split sets and parallel deployment remain outside
this retained v1 contract.

All four POST routes require JSON, the same origin/bearer/session checks, the
4 KiB body bound and strict duplicate/unknown-field rejection:

| Route | Exact request |
| --- | --- |
| `/browser/v1/retained/preflight` | `artifactId`, `sha256`, `sizeBytes`, `command`, `serial`; optional `installOptions` and `expiresInSeconds`. Commands are `apk.inspect`, `apk.install-inspected`, `app.launch-resolved`, `runtime.observe`. Only inspect uses null serial. Install options contain exactly boolean `replaceExisting` and `grantRuntimePermissions`. |
| `/browser/v1/retained/execute` | `operationId` and `commandDigest` from preflight; consumes the existing owner command once. |
| `/browser/v1/retained/status` | The original `operationId` and `commandDigest`; reads its retained state without executing. |
| `/browser/v1/retained/cancel` | The original binding; asks the owner to cancel running work. A preflighted record returns `operation_not_running`. Cancellation is not proof that a dispatched mutation had no effect. |

Retained preparation/execution and transient Core commands share one host
operation gate. Retained status/cancel remain available during execution.
Execution follows owner/application lifetime, not the page connection lifetime.
An execute response can be `outcomeUnknownRecoveryRequired`; an exit-zero ADB
command with incomplete fresh readback never becomes completed. Duplicate execute
returns 409 without claiming `dispatched:false`. Never replay an unknown
operation or prepare a new mutation as its substitute.

The browser saves only small operation references in tab session storage, never
credentials, full results, APKs or private paths. Reload/authentication sends no
operation automatically. Save the ID and digest promptly: owner terminal record
retention is bounded to 24 hours, including unknown outcomes. The original record
and its uncertainty survive host restart through the existing journal. Reading
status does not rerun observation, reconcile or promote an unknown outcome.

An explicit separate **Observe selected runtime (keep original)** action can
prepare and execute a new read-only `runtime.observe` while preserving an unknown
original. It has its own saved binding/status/cancel controls and no automatic
replay. Those later process/focus facts do not rewrite the original launch
receipt or establish application/OpenXR readiness. Unknown mutations keep new
device changes blocked; use the existing typed owner recovery route and its
actual evidence before any further mutation.

## Power and existing supervisors

The bounded awake command already applies stay-on, wake and the exact Meta
proximity hold, followed by independent effective readback. It does not start
a browser renewal timer, replace existing supervisors or grant their custody.
The duration bounds the proximity hold only; stay-on does not automatically
revert at that deadline.
Normal-settings restore is an explicit separate action; closing the page does
not restore settings. Coordinate the exact target, ongoing product work and
current supervisor owner before invoking either action.

The dedicated Fleet provider separately implements a device-resident temporary
shell watchdog with no APK: `nohup` detaches it and it repairs until stopped,
lost or rebooted. It can outlive a page or Windows host. Eight hours bounds
each hold, not the watchdog's total lifetime. It has no boot persistence, and
remote stop requires an accessible ADB transport. This browser registry does
not expose that managed route; its Fleet authority/configuration/custody
requirements remain intact. See [Quest awake control](quest-awake-control.md).

## USB proof and validation

The separately bundled WebUSB proof is a browser-native, explicit chooser and
read-only path. It does not use this host command API to claim USB access.
Browser permissions, driver/interface ownership and headset RSA prompts are
independent; preserve a shared ADB daemon and existing watchdog when the USB
interface is busy. See the interface's agent guide and vendor notices for its
precise source, key retention and cleanup behavior.

Focused backend tests use fake runners only; they never start a server or
contact ADB. They exercise listener/Host/Origin/bearer rejection, strict JSON,
same-handler readback, disconnected endpoint connection, pending/unknown
outcomes, sanitized errors, one-use IDs, capacity and session restart rejection:

```powershell
dotnet test tests/QuestIonAbleFileManager.BrowserHost.Tests --configuration Release
```

Browser, USB and live device validation are separate evidence; source/unit
results establish none of those live effects.
