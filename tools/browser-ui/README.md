# Browser frontend regression fixtures

Run with Node and Playwright 1.62.1 plus its matching Chromium installed:

```powershell
node --test tools/browser-ui/frontend.test.cjs
node --test tools/browser-ui/retained.test.cjs
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
