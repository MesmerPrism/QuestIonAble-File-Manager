# Wi-Fi ADB And Parallel Installation

## Decision

Support the established TCP/IP ADB workflow as an explicit operator action:
bind one stable identity from the USB-authorized headset, read its Wi-Fi
address, switch that headset's ADB transport to a selected TCP port, connect
the exact endpoint, verify that it appears ready in `adb devices -l`, and
require the same identity through that endpoint. Also support connecting and
disconnecting a previously enabled endpoint without resetting the ADB server.

Install fan-out is a bounded set of independent, serial-scoped ADB requests.
One failed headset does not cancel or hide results from the others.

## Scope

- Enable and connect Wi-Fi ADB from one ready, directly connected USB headset.
- Connect or disconnect an already enabled IPv4 address or DNS hostname.
- Display USB and Wi-Fi transports distinctly in device discovery.
- Install one APK on at least two selected Wi-Fi ADB headsets.
- Install one complete base-and-split APK folder on at least two selected
  Wi-Fi ADB headsets.
- Cap parallel work from 1 to 16 installs, with a default limit of 4.
- Preserve one success or failure result for every target.
- Require an explicit confirmation in both the WPF and CLI operator routes.

## Non-scope

- Enabling Quest Developer Mode or bypassing the in-headset ADB authorization.
- Android 11 TLS pairing-code discovery or `adb pair`.
- ADB server reset, restart, port ownership, or daemon recovery.
- Automatic network scanning, subnet probing, or remembered credentials.
- Assuming Wi-Fi ADB survives a reboot, network change, debugging timeout, or
  authorization revocation.
- Treating a folder as a collection of unrelated standalone apps. A bundle
  folder remains one Android package set installed with `install-multiple`.

## Authority And Command Shape

The headset owns Developer Mode, authorization, network state, and whether its
ADB listener is available. ADB owns transport. `AdbClient` owns validated
argument construction, bounded execution, and per-target results.
`OperatorCommand` owns the GUI/CLI-equivalent request.

The enable route performs these operations in order:

```text
adb -s <usb-serial> shell getprop <stable-identity-property>
adb -s <usb-serial> shell ip route
adb -s <usb-serial> tcpip <port>
adb connect <quest-ip>:<port>
adb devices -l
adb -s <quest-ip>:<port> shell getprop <same-stable-identity-property>
```

The first three operations are serial-scoped to the selected USB headset.
`connect` and `disconnect` cannot use `-s` because they establish or remove the
endpoint itself; they are instead scoped to one validated `<host>:<port>`.
The final identity read is scoped to that exact endpoint and must equal the
pre-mutation USB readback. Empty, `unknown`, malformed, mismatched, and stale
endpoint identities fail closed. Machine-readable local output retains only
the lowercase SHA-256 identity digest.
Installation always returns to serial scope:

```text
adb -s <quest-ip>:<port> install <options> <apk>
adb -s <quest-ip>:<port> install-multiple <options> <apk-parts...>
```

No route invokes `adb kill-server` or `adb start-server`.

## Operator CLI

Wi-Fi state changes require the confirmation flag after a human operator has
approved the exact target:

```powershell
questionable-file-manager.exe wifi enable `
  --serial <usb-serial> `
  --port 5555 `
  --confirm-wifi-adb

questionable-file-manager.exe wifi connect `
  --host <quest-ip> `
  --port 5555 `
  --confirm-wifi-adb

questionable-file-manager.exe wifi disconnect `
  --host <quest-ip> `
  --port 5555 `
  --confirm-wifi-adb
```

Parallel single-APK installation:

```powershell
questionable-file-manager.exe apk install-many `
  --serial <quest-a-ip>:5555 `
  --serial <quest-b-ip>:5555 `
  --file <path-to.apk> `
  --parallelism 2 `
  --json
```

Parallel APK-set installation:

```powershell
questionable-file-manager.exe apk install-bundle-many `
  --serial <quest-a-ip>:5555 `
  --serial <quest-b-ip>:5555 `
  --folder <apk-folder> `
  --parallelism 2 `
  --json
```

The CLI exits nonzero if any target fails, while its output still contains the
result for every headset.

## WPF Workflow

1. Connect and authorize a Quest by USB and select its `[USB]` row.
2. Open **Wi-Fi ADB**, confirm the port, and choose **Enable and connect**.
3. Approve the explicit warning. The connected endpoint appears with a
   `[Wi-Fi]` label after refresh.
4. Repeat for each headset, or connect known endpoints from the same tab.
5. Open **APKs**, check at least two Wi-Fi headsets, select the APK or bundle
   folder, set the parallel limit, and choose the matching checked-headset
   install button.
6. Review the exact target list in the confirmation dialog and the per-headset
   result summary after completion.

## Observability

Connection results retain the endpoint, ADB command result, and verified
device row. Parallel installation retains the deterministic APK path set,
requested concurrency cap, and a command result or exception summary for every
serial. The WPF app summarizes all targets; `--json` exposes the same typed
result to agents.

Raw endpoint values and device output are local operator evidence. Do not add
real addresses, serials, APKs, or logs to the repository.

## Validation

- Parser tests select the `wlan0` IPv4 source address and reject unrelated
  interfaces.
- Contract tests require explicit approval and compare every WPF command with
  its exact CLI argument vector.
- Fake-runner tests prove enablement binds identity and reads the address
  before `tcpip`, connects the exact endpoint, verifies discovery, and rejects
  identity mismatch or a ready stale endpoint.
- Concurrency tests prove the configured cap is respected.
- Partial-failure tests prove successful and failed targets are both retained.
- Bundle fan-out tests prove every target receives the complete deterministic
  APK set in one `install-multiple` request.
- Live validation is a separate, operator-approved gate because it changes
  headset Wi-Fi ADB state.

The first approved two-headset run passed; see the
[sanitized live-validation receipt](wifi-adb-parallel-live-validation-2026-07-17.md).

## Mitigations

| Risk | Mitigation |
| --- | --- |
| Wrong USB headset | Require the selected ready USB serial and show it in the confirmation. |
| Wrong network endpoint | Validate one IPv4/hostname plus port, then require the exact endpoint to appear ready. |
| Ready endpoint belongs to another headset | Require the same stable identity before USB mutation and after exact-endpoint connection; expose only its digest. |
| Hidden global ADB mutation | Exclude daemon lifecycle commands; connect and disconnect one endpoint only. |
| Unbounded fan-out | Require an explicit 1–16 limit and use a semaphore around each install. |
| Duplicate install on one headset | Reject duplicate serials before running ADB. |
| Partial failure hidden as total failure | Return and display one typed result per headset. |
| Incomplete split package | Snapshot one folder and send its entire APK set to every target. |

## Next Slice

Add optional TLS pairing-code support only after a separate compatibility and
authorization review. A future durable connection profile may remember friendly
labels, but it must not store private pairing material or scan networks without
an explicit operator action.
