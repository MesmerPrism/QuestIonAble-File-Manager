# ADB Scope And Safety

QuestIonAble File Manager assumes Developer Mode is already enabled and the host
has been authorized in-headset. It does not enable Developer Mode or bypass
Android permissions.

## What File Browsing Means

The app starts at `/sdcard`, the user-visible shared-storage surface. Other
absolute paths may be entered, but Android decides whether the `shell` user can
list or read them. A failure is reported as a permission or path error; the app
does not attempt privilege escalation.

The initial feature set is deliberately asymmetric:

- list is read-only;
- pull copies from Quest to the selected Windows path;
- push copies one explicitly selected Windows file to an explicit remote path;
- delete and recursive mutation are absent.

## APK Export

The export route runs Android package-manager inspection equivalent to:

```text
adb -s <quest-serial> shell pm path <package>
```

One returned path is exported and hashed. More than one path means the install
uses split APKs; the app refuses it because exporting only `base.apk` would be
an incomplete backup.

APK export does not include app data, saves, login state, OBB files, downloaded
assets, licenses, or entitlements. Only export software you own or are allowed
to copy.

## APK Install

Reinstall is enabled by default. Downgrade, runtime permission grants, and
test-only APK admission are separate explicit options. Install errors retain
the Android failure code so signing, version, ABI, or storage problems remain
diagnosable.

The bundle route reads at least two top-level `.apk` files from one selected
folder, orders them deterministically, and passes the entire set through one
`adb install-multiple` request. It does not recurse, and it does not treat a
folder of unrelated standalone apps as a batch queue. Android rejects package
name, version, signing-certificate, or required-split mismatches without a
partial per-file install loop.

## Multiple Devices

Every device-targeting operation is sent with `adb -s <quest-serial>`. The app
does not rely on ADB's implicit single-device selection.

The agent-only `adb forwards --serial <quest-serial> --json` route is the
deliberate inventory exception. It invokes the shared daemon command `adb
forward --list` without `-s`, validates the complete snapshot, then returns
only rows with an exact matching serial. It cannot create, remove, replace, or
test a forwarding record. A matching row is not a device-health, ownership,
reachability, application, or headset-effect claim.

Parallel installation is not an exception to serial scope. The app validates
at least two distinct Wi-Fi ADB serials, bounds concurrent work, and launches
one independently scoped `install` or `install-multiple` request per headset.
Every target receives a result even when another target fails.

## Wi-Fi ADB

Wi-Fi ADB still requires Developer Mode and prior in-headset authorization.
The reviewed enable route starts from one selected, ready USB headset:

1. read one stable nonempty identity on that serial;
2. inspect `ip route` on that serial and select the non-loopback `wlan0` IPv4
   source address;
3. run `tcpip <port>` on that same serial;
4. connect only the validated `<quest-ip>:<port>` endpoint;
5. require that exact endpoint to appear ready in device discovery;
6. read the same identity property through that endpoint and require equality.

The WPF app asks for confirmation before enable, connect, and disconnect. The
CLI requires `--confirm-wifi-adb`, which an agent may use only after operator
approval for the exact target. Connect and disconnect are endpoint-scoped
because they create or remove the serial itself. They do not reset, restart,
or otherwise manage the global ADB server.

The first Wi-Fi slice does not implement TLS pairing codes, subnet scanning,
or credential storage. A connection can disappear after a headset reboot,
network change, debugging timeout, or authorization revocation. See
[Wi-Fi ADB and parallel installation](wifi-adb-and-parallel-install.md) for the
full workflow and validation contract.

## Rusty Kiosk And Reviewed Device Settings

The optional Kiosk integration does not grant the Windows app general control
inside Kiosk. ADB shell may call only the versioned, DUMP-protected provider.
The provider admits a fixed command enum and fixed tag-transfer methods. Tag
import/export never accepts a headset path: the PC sends or reads ordered Base64
chunks, capped at 6 KiB each and 256 KiB total, and both sides verify SHA-256
before Kiosk validates the schema and atomically activates it.

The setup helper receives `WRITE_SECURE_SETTINGS` once from an explicitly
authorized USB connection. The main Kiosk APK does not receive that permission;
it may invoke only its same-signer helper's fixed operations. Meta's Wi-Fi ADB
permission UI remains visible and wearer-controlled.

Keep-awake and CPU/GPU override controls are explicit reviewed commands, not a
generic shell. Keep-awake is reversible, CPU/GPU values are limited to 0–5,
and clearing restores app-controlled values. Each route requires confirmation
and reads effective state back. A mismatch remains pending rather than being
displayed as successful.

## Mutation Synchronization

Every state-changing operator route follows one state model:

1. `sent`: the exact desired state and target are recorded before dispatch;
2. `pending`: transport/result and effective-state evidence are outstanding;
3. `confirmed`: route-specific headset readback matches the desired state;
4. `failed` or `timed_out`: no matching evidence was obtained.

Timed-out wearer prompts remain reconcilable on a later refresh. Read-only
status commands do not create mutation receipts.

## Optional Fleet Hook

Rusty Fleet interop is disabled by default and remains separate from ordinary
file operations. Its v1 CLI contract can observe one exact ready serial, list
one bounded relative path below File Manager's fixed `/sdcard` mapping, or pull
one confirmed file into an operation-owned directory below an operator-approved
local root.

The hook rediscovers the exact serial and USB/Wi-Fi transport before and after
work. Its one remote owner command requires the requested canonical path to be
exactly below the canonical `/sdcard` root, pins a file descriptor, and
revalidates that descriptor before bounded output. It rejects stale observation
digests, traversal, Windows-reserved names, remote symlink/intermediate
indirection, reparse points, hardlinks, delete-pending output, local collisions,
oversized streams, directories passed as files, and ambient mutation. Windows
staging and cleanup use retained handles rather than recursive path traversal.
Ctrl+C and timeouts terminate the bounded process and clean the same owned
handles.

The shipped CLI remains `list`/`pull`/read-only-status only. A Core host may
advertise no-overwrite push only after injecting a current Quest identity and
Manifold authority verifier. That route locks an exact staged input, checks
size/SHA-256 from the same stream, creates remote partial/final descriptors
with no-clobber staging and atomic no-replace hard-link publication, and records
durable live/recovery state. Filesystems without that primitive fail closed. It has no
delete, overwrite, multi-target, or ADB daemon route, and recovery never
retries or cleans remote paths automatically. ADB transport observation is not
proof of Rusty Fleet device identity. See
[Optional Rusty Fleet integration](fleet-integration.md).

The operator-approved staging root must also be ACL-restricted to the
operator/File Manager security context. Fleet treats its operation directories
and replay tombstones as File Manager-owned state; a separate local principal
with delete rights could otherwise erase durable replay evidence after the
owning handles close.
