# Browser USB proof module

The browser host serves checked-in static ES modules and the self-hosted Tango
bundle. Opening the page and enumerating previously permitted devices is inert.
The USB chooser runs only from an operator click. Connecting claims that exact
ADB USB interface and performs ADB authentication; the headset may ask the wearer
to authorize the browser. No headset app is required.

This proof exposes fixed read-only device operations only: canonical serial
(`getprop ro.serialno`), boot ID (`cat /proc/sys/kernel/random/boot_id`) and
`dumpsys battery`. The identity pair is read before and after the battery and
must match. Later reads stay on the same canonical serial and boot; an explicit
reconnect permits a new boot for the same serial. USB descriptors are not
canonical identity and duplicate descriptor serials never merge exact handles.
There is no exported caller-selected shell, path, property, mutation, app
installation, TCP/IP setup, watchdog, driver or ADB-server action.

## UI interface

Import `wwwroot/usb-proof.js` as an ES module. Its exports are:

- `capability()` -> `{ supported, secureContext, state, message }`.
- `getPermittedDevices()` -> a promise of actual permitted descriptors
  `{ selectionId, name, usbSerial }`. Enumeration never connects. An unsupported
  environment returns an empty list; an enumeration API failure rejects.
- `chooseAndRead({ onState } = {})` -> a result promise. Call directly in a click
  handler so the native USB chooser retains browser user activation.
- `read(selectionId, { onState } = {})` -> one fresh read on its existing session.
- `reconnect(selectionId, { onState } = {})` -> close and explicitly reconnect
  the exact selected handle, then read. It never chooses another headset.
- `disconnect(selectionId)` -> close that USB connection and cancel pending work.

Results/state callbacks contain `{ state, message, selectionId?, device?,
reading?, errorCode? }`. A successful reading is `{ serial, bootId,
battery: { level, scale, status, charging } }`; status follows Android's
BatteryManager values, and `charging` is null for unknown status. Device and
identity values are untrusted text; render with `textContent` rather than HTML.

States are `unsupported`, `not-permitted`, `interface-busy`, `auth-prompt`,
`failed`, `connected`, and `disconnected`. `auth-prompt` means authentication is
pending and the wearer should check for a prompt; it does not assert that a
native prompt was observed. `connected` is emitted only after valid identity
and battery readback. A disconnected transport emits an event without polling
or automatic reconnect. Callbacks carry the exact selection ID so a UI can
ignore events for another currently selected device.

Authentication is bounded to 60 seconds and a read to 15 seconds. Response
streams are size-bounded and require shell-v2 exit status. Cancellation and
timeouts close only this page's exact USB handle; unresolved underlying work
retains its handle lock until it settles. No host ADB process is stopped to
resolve an interface conflict. On Windows an existing host ADB client may own
the interface; the page reports the conflict for the operator to resolve.
If disconnect cleanup outlives its two-second UI deadline, the result explicitly
has `cleanupPending: true` and `closureConfirmed: false`; it does not claim the
connection closed. A raw USB close refusal also fails closed. A new connection
cannot start while previous cleanup remains unresolved.

## Browser key lifecycle

`AdbWebCredentialStore` uses Web Crypto RSA keys stored by the upstream library
in origin-scoped IndexedDB: database `Tango`, object store `Authentication`.
Its application name changes the authorization label, **not** the database
namespace. Pages at different paths on one origin can share these keys, so use
a trusted hosting origin. Keys are persistent/exportable browser profile data;
this module never prints, returns, uploads or exports them. Disconnect retains
the key and the browser's USB permission.

To reset local keys, disconnect first and clear this site's browser storage, or
remove its `Tango` IndexedDB database in browser developer tools. This may affect
other Tango applications hosted on that same origin. It does not revoke an
already trusted key on the headset: revoke USB debugging authorizations in the
headset's developer settings separately. Revoke the site's USB device permission
through browser site settings if desired. A different scheme/host/port is a
different origin and may need another wearer authorization.

## Rebuild and test

With Node.js on PATH, run from this directory:

```powershell
npm ci --ignore-scripts --no-audit --no-fund
node build.mjs
node --test tests/*.test.mjs
```

The package lock pins the complete dependency graph. The coherent published
non-beta package set is `@yume-chan/adb` 2.6.4,
`@yume-chan/adb-daemon-webusb` 2.3.2 and
`@yume-chan/adb-credential-web` 2.1.0; their version numbers intentionally differ.
The build uses esbuild 0.28.2, emits no source map or runtime CDN import, and
generates `wwwroot/vendor/tango-adb.js`, `TANGO_NOTICES.txt` and
`tango-packages.json`. These checked-in assets let static hosting run without
npm or a native bridge. The test peer is entirely in memory and proves protocol
integration/target isolation, not compatibility with any particular Quest OS.
Hardware acceptance requires a separate authorized device trial.

Dependency licenses are MIT; preserve the generated notices and the included
AOSP Apache-2.0 attribution when distributing the bundle. The retained
`upstream-adb.NOTICE` was read from Tango tag v2.6.4 at commit
`cdc74fa6b14313ae38c3136f32cf5b0a153a20f0`:
[upstream notice](https://github.com/yume-chan/ya-webadb/blob/cdc74fa6b14313ae38c3136f32cf5b0a153a20f0/adb.NOTICE).
The implementation uses the published package source; matching API references
are [USB manager](https://github.com/yume-chan/ya-webadb/blob/cdc74fa6b14313ae38c3136f32cf5b0a153a20f0/libraries/adb-daemon-webusb/src/manager.ts),
[authentication](https://github.com/yume-chan/ya-webadb/blob/cdc74fa6b14313ae38c3136f32cf5b0a153a20f0/libraries/adb/src/daemon/transport.ts),
[shell process API](https://github.com/yume-chan/ya-webadb/blob/cdc74fa6b14313ae38c3136f32cf5b0a153a20f0/libraries/adb/src/commands/subprocess/shell/spawner.ts),
and [browser credentials](https://github.com/yume-chan/ya-webadb/blob/cdc74fa6b14313ae38c3136f32cf5b0a153a20f0/libraries/adb-credential-web/src/index.ts).
