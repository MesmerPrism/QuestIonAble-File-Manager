# Browser frontend regression fixtures

Run with Node and Playwright 1.62.1 plus its matching Chromium installed:

```powershell
node --test tools/browser-ui/frontend.test.cjs
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

Set `QFM_BROWSER_UI_EVIDENCE` to an external/private output directory to save
desktop host and mobile public-origin screenshots. Product/live-device QA is
separate; these fixtures do not establish hardware behavior.
