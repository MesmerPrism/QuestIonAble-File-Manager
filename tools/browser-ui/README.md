# Browser frontend regression fixtures

Run with Node and Playwright 1.62.1 plus its matching Chromium installed:

```powershell
node --test tools/browser-ui/frontend.test.cjs
node --test tools/browser-ui/retained.test.cjs
node --test tools/browser-ui/retained-deploy.test.cjs
node --test tools/browser-ui/apk-read.test.cjs
node --test tools/browser-ui/transfers.test.cjs
node --test tools/browser-ui/file-push.test.cjs
node --test tools/browser-ui/kiosk.test.cjs
node --test tools/browser-ui/direct-connection.test.cjs
node --test tools/browser-ui/direct-command.test.cjs
node --test tools/browser-ui/direct-tags.test.cjs
node --test tools/browser-ui/direct-staging.test.cjs
node --test tools/browser-ui/direct-upload.test.cjs
node --test tools/browser-ui/direct-install.test.cjs
node --test tools/browser-ui/profiles.test.cjs
node --test tools/browser-ui/fleet-status.test.cjs
node --test tools/browser-ui/diagnostics.test.cjs
node --test tools/browser-ui/diagnostics-launch.test.cjs
node --test tools/browser-ui/package-stop.test.cjs
node --test tools/browser-ui/download.test.cjs
node --test tools/browser-ui/pwa.test.cjs
```

`require('playwright')` uses normal Node resolution, including an explicitly
configured `NODE_PATH` when using an existing external tooling installation.
No dependency download is performed by these tests.

The tests load the actual shipped HTML, modules, CSS and Tango bundle in a
headless browser. Every network request is intercepted and fulfilled or
blocked. The synthetic numeric-loopback and public origins have no server;
there is no host API, ADB process, WebUSB chooser, device or headset effect.
The fixtures use invented targets and credentials only. They exercise DOM
events and visible/storage state for confirmation, rejection, lost response,
exact endpoint completion and public-origin fail-closed behavior.
The public-origin credential form is also checked with application module
loading deliberately blocked, proving its disabled default needs no JavaScript.
The retained fixtures exercise exact review/approval, one-use execution, lost
replies, saved original references, explicit status/cancel, and a separate
read-only observation while an original remains unknown. They never contact
the native retained owner or a device.
Versioned deploy fixtures additionally check explicit capability/version routing,
reviewed install options, legacy reference recovery, rejected crossed responses,
and lost-reply/reload behavior without replay or fallback.
APK read fixtures verify true readiness versus retained preparation, exact and
development-policy separation, structured absence, no automatic fallback,
scope-bound late replies and preservation of unknown mutation references.
Package-stop fixtures check exact serial/package approval, coherent quiescence
and receipt bindings, saved original custody, same-session lookup, cross-card
blocking and no replay after a lost reply or connector restart. These fixtures
use synthetic process facts and do not prove a headset stopped an app.
Kiosk fixtures cover fixed product channels, explicit activity-opening consent,
local-only filtering/highlighting, catalog-bound selection, conditional unavailable
no-ops, original request custody and separate provider facts without reconciliation.
Direct Meta Home fixtures retain the existing null-valued Invoke and original
recovery flow. They verify Kiosk acknowledgement only, never physical Meta Home
foreground or wearer visibility.
Transfer fixtures keep read captures separate from local APK/retained references,
check exact source/target/receipt bindings and reject stale replies. Download
fixtures use explicit mock picker/writable handles with real Chromium streams,
including a synthetic file above the buffered ceiling, to verify incremental
hashing, abort, timeout and close acknowledgement. They do not claim a real OS
picker or final save destination was exercised.

Set `QFM_BROWSER_UI_EVIDENCE` to an external/private output directory to save
desktop host and mobile public-origin screenshots. Product/live-device QA is
separate; these fixtures do not establish hardware behavior.

`pwa.test.cjs` checks the rendered worker template, content identity, static-only
cache boundary and real icon dimensions without opening a listener. The
opt-in real worker test requires an explicitly reserved loopback port:

```powershell
$env:QFM_BROWSER_UI_PORT = '<reserved-port>'
node --test tools/browser-ui/pwa-browser.test.cjs
```

It starts only an inert static/synthetic-session fixture server, installs the
actual worker, checks Chromium installability, stops the server and opens the
cached app/help. It verifies no private/API cache, no automatic reconnect
commands, responsive layout and coherent waiting-worker upgrades. It never
starts QFM's live API, ADB or a USB chooser. It closes its browser/server on exit;
release the port reservation afterwards. Evidence uses the same external
`QFM_BROWSER_UI_EVIDENCE` directory as the DOM fixtures.

Direct-connection fixtures exercise the shipped connect/status/close card with
synthetic acknowledgements only. They check pre-dispatch USB ownership IDs,
opaque scope, expiry/explicit close, pre-existing listener preservation, original
UNKNOWN custody, no replay, private-field exclusion and cross-view blocking.
They do not mint a real credential or enable a listener on a headset.

Direct-command fixtures also cover the two after-restart Wi-Fi ADB preference
commands: literal null input, exact boolean readback and preservation of an
original when proof is malformed, contrary or pending. Full-app fixtures check
lost replies across reload and explicit cached-result recovery while an
unrelated original remains blocked. These are synthetic preference receipts;
they do not change host ADB transports, reboot a headset or approve wearer prompts.

Accessibility and passthrough fixtures validate literal boolean/style/LUT
readback, exact desired values and the complete original Core receipt binding.
The full-app cases preserve a pending original until an explicit typed-result
read confirms it, with one Invoke and no replay. Reported flags and mode are
distinct from real visual output, camera/OS permission or wearer approval.

The separate Wi-Fi ADB request/disable fixtures require the literal reported
enabled flag and exact desired value, preserving pending originals until an
explicit bound result read. The prior fifteen projection shapes stay unchanged.
These fixtures never set up, discover or connect a real host/device transport.

Profile fixtures use synthetic intercepted responses and injected backend stores.
They cover manual/private-file byte input, fixed headers and reviewed ID,
private-field/buffer clearing, explicit replacement/revoke, exact rollback
semantics, lost originals, auth generations and cross-view blocking. They never
open Credential Manager or a real profile, ADB/Fleet provider, host or device.
Local record validity is distinct from connectivity and lost-write identity.

Fleet status fixtures intercept every URL and inject only synthetic status
receipts. They cover explicit target-free reads, unavailable/configured states,
closed safe projections, lost replies, authentication generations, unchanged
sibling UNKNOWN references, no storage/replay, and desktop/mobile controls.
They never resolve real Fleet configuration, metadata or protected replay state.
The real-app suite also verifies that the card is capability-gated and does not
acknowledge generic or profile writes.

Diagnostic fixtures cover reviewed source/target consent, expiry, original
reference persistence before dispatch, explicit status/cancel after reload,
lost preparation or execution replies, crossed authentication generations,
shared uncertainty blocking, separate archive copies and verified Save results.
They use synthetic responses and download handles, with no native capture or
device effects. The full-app suite checks the shipped card's integration with
existing staging and sibling operation references.
