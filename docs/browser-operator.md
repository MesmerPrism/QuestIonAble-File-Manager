# Optional browser operator host

`questionable-file-manager-browser` is a separately started Windows host for a
closed browser projection of existing QFM Core commands. Ordinary host ADB
operations require no headset application. Developer mode, protected debugging
authorization, a suitable USB/network transport and a selected host ADB remain
prerequisites. It does not expose the existing inspected-deployment API or
change that API's registry, journal or deployment security boundary.

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
route. Responses disable caching and set a self-only CSP. Logs are disabled.

Starting the process or loading a page performs no ADB discovery or mutation.
ADB is resolved only on an explicit operator action. This development output
is framework-dependent: retain its sibling assemblies, runtime metadata and
wwwroot; it is not one of the dedicated Fleet provider trust units. No signed
release or installed Windows-service behavior is claimed.

## Typed API

All API routes require `Authorization: Bearer <private-credential>`.

| Method/path | Contract |
| --- | --- |
| `GET /browser/v1/session` | Version, current process session nonce, closed actions and capacity. No device query. |
| `POST /browser/v1/execute` | One strict JSON command. Additionally requires `X-Qfm-Session` from the current session response. |
| `GET /browser/v1/requests/{requestId}` | Original retained running/result state; also requires the session header. Never reruns an action. |

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

Port is an integer 1–65535. Hold duration is an integer 60000–28800000
milliseconds. No caller-selected shell, ADB executable, file path, package,
property, process, Fleet identity, watchdog or command argument is accepted.
Serial-scoped operations first use the same Core discovery command to require
one exact ready serial. Wi-Fi enable additionally retains Core's USB-to-network
stable-identity join. Connect intentionally does not require the disconnected
endpoint to be ready before connection; Core verifies that exact endpoint
afterwards. Its `serial` input is endpoint confirmation, never a claim that
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
