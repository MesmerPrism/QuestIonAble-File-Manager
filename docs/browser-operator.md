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

For a checked development archive with complete shell, licenses and file
checksums, use the [optional connector packaging guide](browser-connector-package.md).
The package has no native apphost or bundled runtime/ADB and changes no signed
installer/update channel. Extracted Windows and WSL Linux runtime checks are
separate from other RID publish targets and macOS runtime validation.

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
advertise `apk.inspect`, `apk.preflight` and `runtime.observe`. Support is not root admission: the first explicit
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
| `fleet.status` | none (Windows Fleet release-status capability) |
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
| `apk.preflight` | `serial`, `artifactId`, `sha256`, `sizeBytes` |
| `runtime.observe` | `serial`, `artifactId`, `sha256`, `sizeBytes`, `verification` exactly `exact` or `development-metadata` |
| `files.push` | `serial`, `artifactId`, `sha256`, `sizeBytes`, `remotePath` (full absolute destination filename), `confirmOverwrite` exactly `true` |
| `files.pull` | `serial`, `remotePath` (one absolute headset file), `fileName` (simple download label) |
| `apk.export` | `serial`, `package`, `fileName` (simple label ending in `.apk`) |

Port is an integer 1–65535. Hold duration is an integer 60000–28800000
milliseconds. CPU/GPU values are integers 0–5 or null to preserve that override;
at least one level must be supplied. Clear removes both overrides. These ordinary
ADB/file actions accept no caller-selected shell, ADB executable, host file path, property, Fleet identity,
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

Body reads have a five-second deadline. Ordinary admitted ADB commands have a
90-second cancellation deadline; staged APK inspection/lease acquisition and
read captures instead share a five-minute lifetime with their bounded runners.
Local profile writes below retain their synchronous owner's real completion
and rollback rather than imposing a false hard timeout on native storage.
Closing the page does not
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
download have a five-minute deadline and enforce 1 GiB per file, 8 retained files
and 2 GiB total per process. The authenticated `artifactStaging` capability
reports `maximumFileBytes`, `maximumTotalBytes`, `maximumFiles` and
`transferTimeoutSeconds`; these are separate from the retained owner's budgets.
A file name is one bounded simple basename; no
slashes, traversal, device names or alternate streams. The UI offers an editable
staged name for source names outside that contract.

`apk.inspect` uses the same immutable Core factory and inspector as the CLI,
bound to that artifact's exact ID, size and lowercase SHA-256. It consumes a
normal one-use request ID. Its response uses `artifactInspection` with the
artifact receipt and Core inspection facts; Core commands and private staging
paths stay inside the connector. The inspection's `path` is only the staged
display filename. A mismatch fails without publishing inspection success.

The shell never caches these routes. Save uses the browser's file picker and a
streaming incremental SHA-256 check where File System Access is supported.
The picker opens directly from the Save click before any fetch. The helper
checks response headers, exact length and final SHA-256 before closing the
selected writable file. An error aborts the writable; a lost close acknowledgement
retains save uncertainty. A five-minute receive/write/verify deadline starts
after the picker. The selected destination and bytes never enter browser storage
or the service-worker cache. The bundled verifier and its MIT notice are fixed
public assets.

Other browsers use a buffered, size/hash-verified download only up to 256 MiB.
That path reports a browser download handoff, not confirmed disk-save completion.
Larger artifacts need a supporting streaming browser or the existing CLI;
unsupported Save sends no download request. A lost upload response never retries; explicitly
clear staged files after reconnecting if local capacity needs recovery. The UI
keeps staged references in tab memory and rejects them when the host session
changes. Process restart invalidates IDs; it does not establish durable crash
recovery or authorize scavenging unrelated files.

Transient staging performs local transport and inspection. The separate retained
workflow below owns inspected single-APK install, launch and runtime observation.
Remaining advanced deployment options use the existing typed CLI/WPF during
migration.

## APK readiness and runtime reads

After staging an APK and selecting one ready target, **Read APK readiness**
calls the same Core preflight factory as `apk preflight`. It reports manifest
SDK/launcher facts, the device API level, installed-byte classification,
exported launcher, checks and readiness for deploy/launch/diagnose. These are
advisory APK/platform facts; they neither approve a mutation nor establish app
or OpenXR readiness. This read is separate from retained-operation preparation.

**Observe exact installed APK** invokes exact Core runtime observation. Package
absence is structured evidence; installed mismatch or unverified bytes cannot
be silently accepted or changed to a weaker policy. **Observe matching development
metadata** is a distinct explicit choice. It compares stable package/version,
APK paths and update metadata around observation, while installed bytes, signer,
unique build and transaction provenance remain unverified. No caller-reported
install reference is accepted by this browser slice.

These actions use the transient one-use ledger, shared gate and current immutable
artifact lease through Core completion and projection, with a five-minute
lifetime. They require no Native API state directory or integrity key. Results
use `artifactRead` with the artifact receipt, serial, kind, verification policy
and typed `preflight`, `runtime` or `development` facts. Native source paths are
replaced with display filenames/private markers, and preflight suggestions
contain only purpose/readiness; private CLI arguments are omitted. Runtime v6
keeps nullable process corroboration and separate activity/focus dimensions.

The UI validates source/target/policy bindings, clears facts on scope changes,
and rejects late responses from an earlier authentication or selection. It never
persists or replays these reads, chooses a fallback verification policy, or
clears an original unknown mutation. An explicit local connector connection
can still read a USB headset while the wider network is offline.

## Upload and push one file

The separate file-upload view stages a chosen file through the same protected
artifact route and capacity limits as APK upload. Its receipt stays in tab
memory and does not replace a selected APK, download capture or retained
operation. Choose one ready headset and a full absolute destination filename,
then explicitly approve that this write can overwrite an existing file. Changing
source, target, destination or authentication clears approval.

`files.push` uses the immutable source lease, current one-use request ledger and
shared operation gate for the complete Core call. It accepts no host path, shell
or replacement command arguments. Its typed `artifactPush` result binds the
staged receipt, serial, destination, `verification: "remote-size"` and
`remoteSizeVerified`, alongside a sanitized Core mutation receipt. Raw command
vectors, host paths and process output are omitted. Completion requires a
matching confirmed receipt and destination size. The Core destination checks
reject observed directories, final symlinks and other nonregular files; these
checks do not establish ancestor custody, atomic publication or remote SHA-256.

The browser saves a small original request reference before sending the write.
Lost, pending or crossed replies preserve uncertainty and block further device
changes. Reload, reconnect and connectivity events never resend. Explicit
original-result lookup uses the recorded request/session independently of the
currently selected target. A restarted host has a new session and cannot recover
the prior in-memory ledger; preserve the reference and use the typed owner/manual
recovery route. A current file read alone cannot prove an earlier write's history
or restore overwritten bytes. Removing transient source bytes does not clear an
unknown write. No Native API configuration or additional journal is required by
this existing transient control family.

## Bounded file pull and single-APK export

Choose an exact ready target and one full headset file path, then **Stage
download**. Package export instead chooses an exact package and an `.apk` label.
These read-only actions use the normal `/execute` one-use request ledger and
shared operation gate. Core rediscovers the selected target, reads one opened
regular remote file through the binary `exec-out` transport. An internal fresh
nonce, bounded size header, exact payload length and completion trailer preserve
binary data while proving the fixed script finished its source checks. The byte
ceiling applies before and during copying; Core independently validates both
wire and payload counts/hashes. Text shell output is not a binary file channel.
The protected store independently hashes the finished bytes before publishing
an opaque receipt. Its five-minute capture deadline includes output verification.
Failure, overflow, cancellation or source/package-path drift cannot publish a
successful partial download. Missing, malformed, extra or mismatched framing
fails closed; remote exit-zero alone is not capture completion.

Results use `artifactTransfer` with `{artifact,serial,sourcePath,packageName,
checksumText}`. Native output paths and process arguments stay on the host.
The checksum describes the captured bytes, not an atomic app-data snapshot.
Export rejects split packages before streaming, rechecks the package path set
afterwards and returns `<sha256>  <filename>\n` checksum text instead of creating
an unmanaged sidecar. It copies neither app data, assets nor entitlement.

Review the capture scope, size and digest, then Save or explicitly Remove that
one captured artifact. These references stay in tab memory and are invalidated
by a new host session. They do not replace the staged local APK or retained
operation references. Lost capture replies can leave bounded local files; use
the existing explicit Clear action for owned unleased bytes. Reconnection never
repeats a capture. File push remains separate: its existing Core readback proves
remote size, not remote SHA-256, and uncertain writes need their own custody.

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
install, resolved launch, exact single-target deploy or runtime observation.
Preparation creates the owner's independent protected copy and commits its
journal before releasing the transient
upload lease. Review package, version, signer, SHA-256, size, target, options and
expiry before approving and executing once. Removing the transient upload does
not remove this retained copy. A present different APK selection blocks approval;
after authentication or source removal, explicitly read the original status
again. Default approval lifetime is 120 seconds, with a maximum of 300 seconds.
A prepared, unexecuted owner record cannot be cancelled; let its approval
expire and explicitly read status before replacing that browser reference.
Single-APK install supports replace-existing and requested runtime permission
options. Downgrade, test-only, split sets and parallel deployment remain outside
these retained contracts. Deploy installs the exact APK and invokes its resolved
launcher once through the existing Core composite. Completion confirms the
installed-byte and resolved-launch effect; app/OpenXR readiness remains unknown.

All four POST routes require JSON, the same origin/bearer/session checks, the
4 KiB body bound and strict duplicate/unknown-field rejection:

| Route | Exact request |
| --- | --- |
| `/browser/v1/retained/preflight` | `artifactId`, `sha256`, `sizeBytes`, `command`, `serial`; optional `installOptions` and `expiresInSeconds`. Commands are `apk.inspect`, `apk.install-inspected`, `app.launch-resolved`, `runtime.observe`. Only inspect uses null serial. Install options contain exactly boolean `replaceExisting` and `grantRuntimePermissions`. |
| `/browser/v1/retained/execute` | `operationId` and `commandDigest` from preflight; consumes the existing owner command once. |
| `/browser/v1/retained/status` | The original `operationId` and `commandDigest`; reads its retained state without executing. |
| `/browser/v1/retained/cancel` | The original binding; asks the owner to cancel running work. A preflighted record returns `operation_not_running`. Cancellation is not proof that a dispatched mutation had no effect. |

The configured capability advertises `contractVersions`; exact deploy requires
`questionable.file_manager.local_api.v2`. Legacy hosts without this advertisement
retain the v1 choices. V2 routes use the same private owner, journal, key and
shared gate, with no additional listener:

| Route | Exact request |
| --- | --- |
| `/browser/v2/retained/preflight` | `contractVersion` exactly `questionable.file_manager.local_api.v2`, `artifactId`, `sha256`, `sizeBytes`, `command` exactly `apk.deploy`, `serial`; optional `installOptions` and `expiresInSeconds`. Options contain exactly boolean `replaceExisting` and `grantRuntimePermissions`. |
| `/browser/v2/retained/execute` | The v2 `contractVersion`, original `operationId` and `commandDigest`. |
| `/browser/v2/retained/status` | The same v2 version and original binding; never dispatches. |
| `/browser/v2/retained/cancel` | The same v2 version and original binding; requests cancellation with owner acknowledgement. |

The v2 preflight response adds normalized `installOptions` to the existing owner
receipt. This echoes the exact request forwarded to Core after its bound durable
acknowledgement; the v2 digest owns the immutable option binding. Every lifecycle
response must match the original version, ID, digest, command and expiry. Wrong
versions reject before executing, expiring or cancelling another version's work.
The UI saves the v2 contract in its small original reference and requires explicit
version selection for manually entered references. Legacy saved references without
a version remain v1. Reconnecting or changing the operation selector never changes
the original's version or authorizes a fallback.

Mixed v1/v2 rows share the authenticated journal and anchor. Older owner binaries
reject v2 rows; preserve the current qualified owner for recovery rather than
starting an older binary against that state or replacing the state/key. The
separate runtime investigation remains a v1 read-only operation and preserves
either version's original unknown mutation.

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

## Retained private APK diagnostics

On a configured Windows NTFS companion, stage one base APK and select one ready
headset. **Prepare exact diagnostics** copies and inspects the source in the
same retained owner used by v1/v2. Review the APK identity, size, SHA-256 and
target, then approve private capture once before the short preparation expires.
Capture verifies the exact installed APK; it never installs, launches or stops
an app. The ZIP can contain private device/package facts and logs.

The v3 contract is exactly `questionable.file_manager.local_api.v3`. These POST
routes require the same bearer, current session and exact-origin admission as
other browser routes. JSON bodies have the existing 4 KiB/five-second receive
limits and reject duplicate, unknown or encoded fields.

| Route suffix under `/browser/v3/retained/` | Exact fields |
| --- | --- |
| `preflight` | `contractVersion`, `command:"apk.diagnose"`, `artifactId`, `sha256`, `sizeBytes`, `serial`; optional bounded `expiresInSeconds`. |
| `execute`, `status`, `cancel` | `contractVersion`, original `operationId`, original `commandDigest`. |
| `output` | The same original binding plus published `outputId`, `sizeBytes` and `sha256`. |

There is no caller output path, package/UID/PID/filter, shell input or install
policy. Preparation is not installed readiness proof. Core owns bounded capture,
the authenticated journal and archive publication. A completed/partial capture
describes its evidence set and leaves app, OpenXR and wearer readiness unknown.
See the [Core v3 contract](local-api.md#v3-retained-diagnostics-in-core) for
capture limits, quota, retention and output custody.

The card saves only a small allowlisted original reference in tab session
storage, before preparation and again before execution. It stores no APK,
logs, raw result, credential or output path. Lost preparation may leave an owner
row whose ID the browser never received; preserve that uncertainty for owner
recovery. Reload, reconnect, selection and expiry send no diagnostic operation.
Read original status or request cancellation explicitly, using the saved
operation/digest regardless of the current upload or selected target. Status of
another operation never clears uncertainty; cancellation is not rollback.

After publication, **Prepare ZIP download copy** binds the original output ID,
size and SHA-256, holds its native read lease and stages a separately verified
`apk-diagnostics.zip` copy. Save uses the existing streamed verifier, or bounded
buffered download with its weaker final-save claim. Copying never captures
again. Copy receipts stay only in memory/current host session. A lost copy reply
can leave a bounded transient copy; explicit staging Clear removes only owned
unleased transient files. Remove/Clear never deletes the retained archive or
clears the diagnostic original. New archive reads end at owner retention expiry;
an already held read may finish. UNKNOWN outputs have no browser force/forget
or destructive cleanup route.

## Optional Kiosk catalog and selection

Kiosk is optional. Choose one ready target and exactly `stable` or `labs`; the
channel fixes package, activity and provider authority. **Read passive installation
facts** queries package/control-contract facts without requesting an activity.
Reported versions, helper/control flags and provider availability establish neither
fresh signer bytes nor application/connection readiness.

**Open Kiosk and read catalog once** requires explicit activity-opening consent.
Local filtering and row highlighting send no requests. **Select reviewed row on
headset once** requires separate consent and one unique installed, launchable
key/package from a completed catalog in this host session. Fresh reply key/package
and the bound Core marker must match. This card sends no launch command. Legacy
normal/guarded launch readback checks guard state and cannot prove that a reviewed
named app launched; that projection remains separate work.

All four actions use the authenticated existing execute/one-use ledger and shared
gate. Every request includes `requestId`, `action`, `serial` and `productChannel`:

| Action | Additional exact fields |
| --- | --- |
| `kiosk.installation` | None; passive read. |
| `kiosk.catalog.open` | `confirmed` exactly `true`. |
| `kiosk.select` | `catalogRequestId`, `key`, `confirmed` exactly `true`. |
| `kiosk.original-result` | `providerRequestId`, `expectedCommand` exactly `status` or `select`; `key` is required only for `select`. |

The host derives an opening/selection provider ID as `pc-` followed by the outer
browser GUID without dashes. The UI saves both IDs before dispatch. Core's new
closed routes bound each process output to 1 MiB and the browser operation to
90 seconds. Core caps catalog rows at 512, and the browser projects at most 128;
truncation stays explicit. `kiosk` returns typed installation, catalog/selection,
provider-state and activity-request facts beside a bound mutation receipt, with
raw Core results, native vectors, tag-file paths and Direct credentials omitted.
Activity dispatch acknowledgement does not prove foreground or app readiness.

If the operator is unavailable, a verified installation-only result with no
request admission/activity and a confirmed conditional marker completes the
check without pretending a catalog or selection exists. The UI clears stale
catalog authority; selection was not performed. Rejection, pending, crossed or
uncertain results after possible admission preserve the original and block other
mutations. Page loss does not cancel admitted work; nothing automatically retries.

Explicit original browser GET reads only its current-process ledger. A changed
host session cannot recover that ledger. **Read original provider facts (keep
original)** uses the saved provider ID/command/target/channel in a new read-only
lookup, without admission or activity opening. Completed provider facts stay
separate and never clear or rewrite an unknown original. An incomplete provider
response cannot distinguish missing from queued and proves no absence of effects.
Save the reference and use the qualified typed owner recovery route when needed.
These actions require neither protected artifact staging nor Native API state/key.

The new CLI routes use the same factories and preserve historical `kiosk status`
behavior:

```powershell
questionable-file-manager.exe kiosk installation --serial <exact-target> --product-channel stable --json
questionable-file-manager.exe kiosk catalog-open --serial <exact-target> --product-channel stable --request-id <pc-guid> --confirm-kiosk-control --json
questionable-file-manager.exe kiosk select --serial <exact-target> --product-channel stable --request-id <pc-guid> --key <returned-key> --confirm-kiosk-control --json
questionable-file-manager.exe kiosk original-result --serial <exact-target> --product-channel stable --request-id <original-pc-guid> --expected-command status --json
```

Direct Link, setup/provisioning, tag transport, launch/options and secondary controls
retain their existing typed CLI/WPF alternatives during migration.

## Optional USB Direct connection

The Direct card opens one short-lived authenticated Kiosk operator connection
from an already authorized **classic USB** target and explicit Stable/Labs
channel. This does not enable Wi-Fi ADB or restart the shared daemon. The host
must also be able to reach Kiosk's issued HTTP endpoint; the endpoint and
credential never cross into the browser. Native retained configuration,
protected artifact staging and Fleet/profile enrollment are independent.

Explicit connect performs the existing Core USB bootstrap and adoption:
authenticated status, completed typed Kiosk Status and signed staging inventory.
Only that complete acknowledgement publishes a connection handle. “Connected”
describes the adopted operator transport; it establishes no app foreground,
readiness or fresh installed-signer proof. The initial card exposes no generic
Invoke, launch, file, install, tag or manual-pairing command.

| Action | Exact request fields beyond `requestId` and `action` |
| --- | --- |
| `kiosk.direct.connect-usb` | `serial`, `productChannel:stable|labs`, `confirmed:true` |
| `kiosk.direct.status` | `connectionId` |
| `kiosk.direct.close` | `connectionId`, `confirmed:true` |

These use the existing execute route, one-use request namespace, shared gate
and original-result GET. No body accepts an endpoint, credential, private
session/generation, provider authority or arbitrary command. The USB operation
ID is `usb_` plus the connect request UUID without hyphens and is retained before
the single POST. Each opaque connection handle stays bound to that host session,
connect request, exact target and product. Status and close use this saved scope,
independently of the currently selected headset/channel.

Responses have `result:null` and the optional typed `kioskDirectConnection`
projection. Actual Core USB bootstrap, Direct adoption and cleanup receipts
remain distinct; no browser wrapper fabricates a standard headset mutation
receipt. Raw Direct results, endpoint, key, private session/generation, staging
inventory names, native arguments and exception text are omitted.

**Read signed status** is explicit and never bootstraps or adopts again. It does
not acknowledge a lost connect/close response. **Close this connection** erases
the native key and awaits Core cleanup: an owned listener must be confirmed
disabled and stopped on its post-disable generation; a pre-existing wearer
listener is preserved. Preservation does not mean that listener stopped. An
incomplete cleanup remains `cleanup_unknown`; repeated close is Core's memoized
result, not an implicit fresh cleanup attempt.

Expiry revokes use of the handle and erases its key. The nonsecret cleanup
reference remains so explicit close can still clean up an expired connection.
Expiry itself sends no USB cleanup command. Orderly host shutdown waits for its
active operation, revokes the key, and attempts cleanup under the same shared
gate. A bounded failure to acquire cleanup custody reports a fixed
incomplete-cleanup diagnostic rather than overlapping another operation.
A process crash or lost USB transport can still leave cleanup uncertain.

Ordinary bootstrap process reads are capped at 1 MiB with strict UTF-8 and no
unexpected diagnostics; the existing sensitive byte parser retains its 16 KiB
limits and buffer clearing. The operation uses the existing 90-second browser
deadline, followed when needed by Core's independent bounded cleanup. A page
disconnect cannot cancel admitted resource custody or release its gate early.

Tab session storage holds only the small original operation and current
connection references, allowing an explicit close after reload. No connection
facts, API result or credential enters the offline cache. Loading, reloading,
authenticating, changing selection and expiry send no operation automatically.
Late/crossed or lost effect replies preserve UNKNOWN and block further device
changes. Same-host-session original GET can recover its actual acknowledgement;
status, expiry and separate cleanup do not rewrite that original. A changed
host session cannot restore the old in-memory client, ledger or key. Save the
reference for owner investigation; this initial slice adds no public saved-ID
cleanup or credential-restoration route. Do not reconnect to infer the old
outcome. The existing [typed Direct CLI](kiosk-direct-authorized-usb.md) remains
the alternative for the other Direct workflows and its own explicitly scoped
live-session operations.

## Direct commands for Kiosk's current selection

The separate **Kiosk Direct commands** card uses the acknowledged opaque
connection above. Its nine commands are `status`, `show-controls`, `show-apps`, `reload`,
`check-setup-helper`, `set-launch-requirement`, `cancel-pending-launch`,
`launch-normal` and `launch-kiosk`. Only `set-launch-requirement` accepts a
value: exactly `any`, `wifi-on` or `wifi-off`. All require explicit confirmation
because even a status command creates a remote logical request.

Reload accepts no value and requires the same explicit confirmation. Only the
original command's bound, accepted and completed typed result with Core's
confirmed receipt acknowledges the provider reload. This does not prove a
fresh catalog, foreground, application or OpenXR readiness. A lost reply uses
the same explicit original-result lookup and never repeats Reload.

Launch addresses Kiosk's current selection at execution time. A previous
catalog selection is a separate operation; this card cannot bind an atomic
select-and-launch to a named app. The typed acknowledgement observes the guard
mode and requires `pendingRequirementLaunch:false`. A pending wearer requirement
or missing field remains unresolved. Foreground, successful app launch and
application readiness remain unknown. Changing a launch requirement sets the
selection's preference; it does not change Wi-Fi or approve a wearer prompt.

The existing execute route adds these closed actions:

| Action | Fields beyond `requestId` and `action` |
| --- | --- |
| `kiosk.direct.invoke` | `connectionId`, `command`, `value` (literal null except the requirement), `confirmed:true` |
| `kiosk.direct.request-status` | `connectionId`, `originalRequestId` |
| `kiosk.direct.request-result` | `connectionId`, `originalRequestId` |
| `kiosk.direct.request-cancel` | `connectionId`, `originalRequestId`, `confirmed:true` |

The logical ID is `kiosk_` followed by the Invoke browser UUID without hyphens.
It is saved before the sole POST and derived independently by the host. Recovery
resolves that original in the same request ledger and connection, retaining its
command and value. Callers cannot supply a replacement logical ID or use a new
connection to recover it. The additive Core `RequestResult` factory uses only
the existing signed result GET and shared operation-specific readback; it never
admits or invokes a request.

The optional `kioskDirectCommand` projection contains bounded typed facts and
actual Core receipt identity, with `result:null` and `mutation:null`. A lifecycle
state alone never clears the original, including `confirmed`, `cancelled` or
legacy Core `Failed` for remote `unknown`. Only the original's exact typed result
and matching readback can acknowledge its effect. Explicit cancellation has a
separate saved reference and acknowledgement; it does not prove rollback.
`cancel-pending-launch` is a new Invoke and cannot clear an older logical request.

No command is sent on loading, authentication, form changes or expiry. Small
nonsecret Invoke/cancellation references preserve UNKNOWN across reload; they
contain no credential or full result. Connection close remains available for
cleanup of this same connection while its command is unresolved. Closing erases
the key and may end result recovery; listener cleanup never acknowledges a
command. Expiry and host restart cannot reconstruct the old credential.

## Private Kiosk tag backup

With an adopted USB Direct connection and protected Windows staging, **Stage
private tag backup** reads Kiosk's signed tag document through the existing Core
export. It validates the v1/v2 schema and 256 KiB limit, preserves exact UTF-8
bytes, then stages `app-tags.json` with independently checked size/SHA-256. It
never imports tags, invokes Reload or installs an app. The document can contain
private app labels and tags; keep it private.

The existing execute route accepts exactly
`{requestId,action:"kiosk.direct.tags-export",connectionId}`. There is no caller
path, output name, raw document, credential or confirmation field. The same
owner, operation gate and immutable request ledger apply. A fresh signed status
binds the adopted session/generation; the read and output lease stay owned until
the protected store acknowledges the exact copy. The session omits this action
on hosts without configured staging support. Use the existing typed Direct tag export
or Windows companion as the alternative.

A completed response has `result:null`, `mutation:null` and a bounded
`kioskDirectTags` projection: original connection/connect/USB IDs, serial,
channel, schema, size/SHA, artifact receipt and actual Core export receipt.
Raw tags, native paths, credentials and full provider status never appear in
that projection. Completion acknowledges the captured copy, not current tags,
foreground, application or OpenXR readiness.

Loading, authentication, connection changes and expiry send no export. A lost
reply may leave bounded transient bytes. **Read cached export result** looks up
only that original browser request/session and scope; it never performs another
provider read. These read references stay in tab memory and disappear on reload.
They are separate from every effect/UNKNOWN original and cannot reconcile one.
A fresh export always requires another explicit click.

Save, checksum and Remove address the exact prepared copy. Under the same host
and authentication, the historical copy remains usable after its source
connection closes or expires; its original scope stays visible. Save uses the
existing streamed verifier or bounded buffered handoff, retaining their different
final-save claims. Remove and the same host's staging Clear affect owned
transient bytes only. Clear invalidates cached copy reuse; export again explicitly
if needed. No document, credential or API result enters browser storage or the
offline cache. Host restart invalidates the transient receipt. The existing
Core/CLI/WPF file-output API remains available.

## Optional Windows Fleet release status

The **Get Fleet** card performs only the typed `fleet status` read.
The session's `fleetInstallerStatus` capability reports Windows implementation
support; it does not probe configuration, trusted metadata, installed services
or replay state. Only **Read trusted release status** activates the dedicated lazy
owner. No ready device, ADB client, connectivity profile, Direct connection or
Native API configuration is required. Other platforms show the Windows owner
alternative. No guided-install route or button is exposed.

The read uses the host's existing trusted release source and channel. It may
fetch signed metadata and open protected local replay evidence; this is not an
offline-only or filesystem-free observation. The owner verifies its normal
trust and replay boundary. The browser accepts no source URL, path, release
selector or trust override. A configured source can be Stable or Labs; the
card reports the observed host configuration without changing it.

`POST /browser/v1/execute` accepts exactly `requestId` and `action:"fleet.status"`.
A completed response has `result:null`, `mutation:null` and
`fleet:{operation:"status",status:{...}}`. The closed status fields are
`schema`, `status`, `configured`, `sourceKind`, `product`, `version`, `channel`,
`descriptorId`, `descriptorSha256`, `highestHandoffVersion`, `lastOutcome` and
`observedAtMs`. An unconfigured owner returns the completed `not_configured`
observation with null release/history fields. Configured status is `ready`,
`already_handed_off` or `not_newer_than_last_handoff`. These are release/replay
facts, not installed-service health, connectivity, enrollment or acknowledgement
of any previous operation. Unknown owner history text becomes `unknown`.

Failed reads return only fixed configuration, release, replay, timeout,
cancellation or binding codes. Raw owner results, paths, URLs, process output
and exception text are omitted. The existing five-second body and 90-second
read deadlines remain unchanged. The shared ledger and operation gate retain
the read until its task ends; duplicate IDs cannot dispatch it again. A lost
reply creates no mutation reference, and the UI stores no status or request in
browser storage. A separate explicit read is allowed while an older mutation
is unknown, but never clears or reconciles that original. Reload, authentication,
target changes and online events send no Fleet request. See the
[Fleet installer owner guide](fleet-installer-handoff.md) for the separate typed
Windows CLI/WPF guided-install workflow and its protected replay authority.

## Local Windows connectivity profiles

The optional **Fleet connectivity profiles** card manages only File Manager's
current-Windows-user records under `QuestIonAbleFileManager/QuestConnectivity/`.
It lists IDs, checks stored record validity, imports/creates, explicitly replaces,
and revokes one reviewed ID. It does not connect to a headset, test a listener,
activate a Fleet provider, choose a default profile or enroll a transient USB
Direct session. No ready headset, ADB executable, protected artifact staging,
Native retained configuration or Fleet installer setup is required.

`GET /browser/v1/session` reports `connectivityProfiles` with supported/code/
reason/alternative and limits of 4096 private input bytes and 128 returned list
entries. Supported means Windows composition is available, not that a store or
connection probe passed. Other platforms report
`windows_credential_manager_required` and the Windows companion/CLI alternative;
there is no plaintext credential-store fallback.

Ordinary `profile.list`, `profile.status` and `profile.revoke` use the existing
execute endpoint. List has only `requestId` and `action`; status adds canonical
`deviceId`; revoke adds that ID and `confirmed:true`. List returns an explicitly
capped snapshot with full `totalProfiles` and `truncated`; this does not bound
native enumeration work or imply an empty store on failure. `enrolled` means a
parsable local record, not connected, authenticated or ready. Revoke removes only
that current-user record; it does not stop a listener or revoke remote credentials.

Private import uses **only** `POST /browser/v1/profiles/import`, never generic
execute, artifact staging or a host file path. Its body is the existing strict
[owner enrollment document](quest-connectivity-provider.md#private-profile) as
1–4096 UTF-8 bytes. It requires these single-valued application headers in
addition to the usual bearer/session/origin admission:

| Header | Value |
| --- | --- |
| `X-Qfm-Request-Id` | One nonzero UUID-D |
| `X-Qfm-Profile-Device-Id` | The exact reviewed canonical lowercase ID |
| `X-Qfm-Confirm-Profile-Write` | `true` |
| `X-Qfm-Replace-Existing` | Explicit `false` for create or `true` for replacement |

Unknown profile options, ambiguous headers, encoded bodies, invalid owner
documents and a document/header ID mismatch reject before store activation.
The Core helper validates the existing envelope and returns only its device ID;
it does not open the store. The server then feeds the same owned bytes to the
existing typed stdin import. Manual fields construct the same fixed namespace
document; a browser-selected private JSON file is a bounded byte input, not a
claim that the host's native `--file` ACL/handle checks were performed.

Writes require explicit scope-bound approval. Create uses `replaceExisting:false`;
an existing-record rejection never grants replacement automatically. Deliberate
replacement has its own exact-ID approval. Successful import requires Core's
exact stored-byte readback and parsing. Only its bound original confirmation,
specific pre-write replacement rejection, or confirmed exact rollback can
acknowledge that original. Uncertain rollback, revoke readback failure, crossed
receipts and generic admitted errors remain UNKNOWN. A successful new status,
including `enrolled` or `absent`, cannot establish the identity of a lost private
write or clear it.

The same one-use request ledger and shared operation gate apply. Private receive
has a five-second bound; after admission, Core's actual verification/rollback
owns completion. Synchronous Credential Manager calls are not interrupted by a
browser timeout. Page loss does not release custody early. Owned input buffers
are cleared before gate/drain completion, and orderly shutdown drains admitted
profile changes before other cleanup. Cached outcomes contain only sanitized
profile receipts, fixed reason/rollback codes and nonsecret routing metadata;
top-level `result` and headset `mutation` remain null.

Only `{requestId,sessionId,action,deviceId,replaceExisting?}` enters tab reference
storage before a change. The card clears private USB/endpoint/code/file inputs
and owned document arrays after attempts, cancellation, authentication changes
and visibility/page departure. It never saves a credential, endpoint, USB serial,
private filename/body/hash, list, full result or secret-derived digest. JavaScript
and runtime/network internals can contain immutable temporary copies; the UI
does not claim deterministic erasure of those copies or same-user process
isolation. See the owner guide for the Windows identity boundary.

Reload, authentication, selection and capability changes send no profile call.
Manual original GET uses only the saved request/session and refuses replies from
a crossed authentication generation. A host restart loses that transient ledger;
preserve UNKNOWN and the small reference for owner investigation. The existing
`connectivity-profile` CLI remains the typed alternative. Tests use injected fake
stores and input readers; no live Credential Manager CRUD is implied by DOM,
route or capability checks.

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
