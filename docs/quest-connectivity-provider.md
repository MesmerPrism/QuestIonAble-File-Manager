# Quest Connectivity Provider

QuestIonAble File Manager is the narrow Windows execution provider for Fleet's
Quest Wi-Fi ADB policy. Fleet owns target selection, operator confirmation,
Manifold authorization, scheduling, and its public per-target ledger. File
Manager owns only private target resolution and the reviewed effects:

- call Rusty Kiosk's signed direct operator so its same-signer setup helper can
  apply the on-device Wi-Fi ADB setting or after-boot request preference; or
- enable classic `adb tcpip 5555` from one exact USB-authorized serial and
  verify the one resolved endpoint.

Rusty Kiosk remains the privileged on-device effect owner. Termux remains an
evidence consumer. The provider does not expose raw ADB, arbitrary Kiosk
commands, an endpoint, a pairing code, or a caller-supplied serial.

## Closed Actions

The request action is one of:

- `status`;
- `request_wireless_adb`;
- `enable_request_after_boot`;
- `disable_request_after_boot`;
- `disable_wireless_adb`;
- `enable_classic_tcpip_from_usb`.

The modern route maps only to Kiosk's fixed `status`, `request-wifi-adb`,
`enable-wifi-adb-after-boot`, `disable-wifi-adb-after-boot`, and
`disable-wifi-adb` commands. It never automates or accepts Meta system UI.

The classic action is intentionally separate. It first captures one stable,
nonempty Android device identity through the exact USB serial, then runs
`adb -s <usb-serial> shell ip route` to resolve `wlan0`, followed by
`adb -s <usb-serial> tcpip 5555`, connects only the resolved endpoint, and
requires that exact endpoint to appear ready in fresh ADB device discovery.
It reads the same identity property through the network endpoint and requires
exact equality before confirming. A ready but stale endpoint for another
headset is rejected. Only the identity digest enters provider evidence.
It never resets or restarts the shared ADB server.

## Private Profile

Fleet supplies only its bounded `device_id`. File Manager resolves that ID
through the current Windows user's Credential Manager target:

```text
QuestIonAbleFileManager/QuestConnectivity/<device-id>
```

The credential blob is strict UTF-8 JSON:

```json
{
  "schema": "questionable.file_manager.quest_connectivity_profile.v1",
  "device_id": "<device-id>",
  "usb_serial": "<usb-serial>",
  "endpoint": "http://<quest-ip>:39873/",
  "pairing_code": "<kiosk-pairing-code>"
}
```

The dedicated provider has no profile write or enrollment route. Missing,
malformed, mismatched, non-USB, non-HTTP, or wrong-port profiles fail closed
before an effect owner is called. Profile bytes and pairing characters are
cleared from the provider's owned buffers after use.

Windows Credential Manager protects the profile at the current-user boundary,
not from other processes running as that same Windows user. Such a process can
read the generic credential or invoke this provider. The provider request does
not contain a Manifold signature or other cryptographic caller proof, so File
Manager cannot distinguish Fleet from another same-user caller. Deployment
must isolate the Fleet/provider Windows identity when that distinction is
required. A future stronger contract needs a one-use signed launch capability;
process ancestry or caller-supplied authority labels are not substitutes.

## Independent Facts

The receipt schema is
`questionable.file_manager.quest_wifi_adb_receipt.v1`. It keeps these facts
independent:

- the typed request reached and was accepted by the effect owner;
- the Kiosk-owned setting has the requested readback;
- the after-boot request preference is enabled or disabled;
- wearer approval is `pending`, `unknown`, or `not_applicable`;
- whether File Manager independently discovered a listener for its route.

`adb_wifi_enabled=1` proves only the setting. It does not prove that the wearer
accepted Meta's protected prompt, that a current TLS listener exists, or that
Termux can use loopback ADB. Consequently the Kiosk route reports
`listener_discovered=false`. A delivered wireless request remains
`wearer_approval=pending` even when Kiosk reports the setting applied.
Termux fields are deliberately absent from this File Manager-owned receipt;
only Fleet's separate signed Termux observation route may report Termux
usability.

Classic TCP/IP confirmation proves that the Windows ADB client discovered the
exact ready endpoint. It still does not prove the independent Termux loopback
gate, which remains outside this receipt.

The receipt never returns the USB serial, endpoint, pairing code, raw Kiosk
JSON, raw ADB output, command arguments, local executable path, or Credential
Manager target. Those private facts contribute only to a lowercase SHA-256
evidence digest.

## Dedicated Fleet Artifact

The release artifact is
`questionable-file-manager-connectivity-provider.exe`, published directly from
`QuestIonAbleFileManager.FleetConnectivityProvider`. Its only accepted,
case-sensitive argument vector is:

```text
integration quest-connectivity --json
```

It reads one strict snake-case JSON request with schema
`rusty.fleet.quest_wifi_adb_owner_invocation.v1` from standard input. The input
is capped at 16 KiB; unknown or duplicate properties, invalid identifiers,
unsupported actions, zero identity revisions, windows longer than two minutes,
and expired requests are rejected before profile or ADB initialization.
Within one provider process, each request ID and operation ID is admitted once;
duplicates fail before profile lookup or effect dispatch. The shipped
single-request process exits after one result, so this guard prevents replay
inside an embedded/reused host but does not create durable cross-process replay
protection or Manifold authorization.

The result envelope schema is
`questionable.file_manager.quest_wifi_adb_provider_response.v1`. Exit codes are
`verified=0`, `failed=1`, `rejected=2`, `pending=3`, and `cancelled=4`.
Standard error remains empty.

Publish and test the trust unit with:

```powershell
pwsh -NoProfile -File ./tools/Test-FleetConnectivityProviderArtifact.ps1
```

The gate produces an ignored self-contained single-file `win-x64` executable
and receipt, proves broad commands reject before initialization, proves a
valid request fails closed without its private profile, and proves an isolated
framework-dependent apphost cannot substitute for the release artifact. Fleet
must pin the receipt's lowercase SHA-256. The gate creates and verifies a
distinct private `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for every staged launch;
Fleet must do the same in production.

Host tests are synthetic and do not contact or mutate a headset. Live
activation is a separate, explicitly approved, serial-coordinated validation.
