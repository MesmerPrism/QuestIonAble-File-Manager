# Optional Fleet Installer Handoff

## Decision

QuestIonAble File Manager may help an operator obtain Rusty Fleet, but it must
not become Fleet Manager. The **Get Fleet** tab and `fleet` CLI routes are a
distribution-only bootstrap. Core consumes one preconfigured release source,
verifies one exact Fleet installer, asks that installer for its non-mutating
plan, and opens its visible guided experience.

Fleet continues to own installation semantics, device enrollment, device
identity, discovery, Manifold authority, Wi-Fi/ADB/hotspot behavior, credentials,
scheduling, and every managed fleet operation. The handoff works without ADB
and never initializes it.

## Exact Operator Routes

The WPF buttons and CLI use the same immutable `OperatorCommand` values:

```powershell
questionable-file-manager.exe fleet status --json
questionable-file-manager.exe fleet install --confirm-fleet-install --json
```

Those argument vectors are exact. There is no option for a URL, executable,
channel, installer argument, credential, device, ADB path, network action,
hotspot action, quiet mode, or elevation. The install factory and CLI reject
the route without explicit operator confirmation.

`status` is read-only with respect to Fleet and Windows installation. It
downloads and verifies the descriptor and reads File Manager's local
replay/downgrade state. `install` performs the verified handoff.

## Deployment Configuration

The deployment owner configures the consumer trust boundary outside the CLI:

| Environment variable | Meaning |
| --- | --- |
| `QUESTIONABLE_FILE_MANAGER_FLEET_RELEASE_DESCRIPTOR` | One clean HTTPS descriptor URI under the reviewed `github.com/MesmerPrism/rusty-fleet/releases/download/…` or `mesmerprism.github.io/rusty-fleet/…` path |
| `QUESTIONABLE_FILE_MANAGER_FLEET_INSTALLER_STATE` | Local private state/staging directory |
| `QUESTIONABLE_FILE_MANAGER_FLEET_DESCRIPTOR_PUBLIC_KEY` | PEM file containing the trusted RSA descriptor public key |
| `QUESTIONABLE_FILE_MANAGER_FLEET_DESCRIPTOR_SIGNER_SHA256` | Lowercase SHA-256 of that key's DER SubjectPublicKeyInfo |
| `QUESTIONABLE_FILE_MANAGER_FLEET_INSTALLER_SIGNER_SHA256` | Lowercase SHA-256 of the exact DER Windows signer certificate |
| `QUESTIONABLE_FILE_MANAGER_FLEET_CHANNEL` | Pinned descriptor channel; defaults to `stable` |

If the descriptor variable is absent, the feature reports `not_configured` and
ordinary File Manager behavior is unaffected. A partial or invalid
configuration fails closed.

An absolute local descriptor path is accepted only when
`QUESTIONABLE_FILE_MANAGER_FLEET_ALLOW_LOCAL_FIXTURE=1`. This exists for
offline validation. The fixture's installer must be the sibling file named
exactly `RustyFleet-Setup.exe`; production deployments use HTTPS. Do not commit
private paths, private keys, certificates, installer binaries, or live
configuration.

The Windows identity that launches File Manager can replace environment
configuration and its referenced trust files. Deployment must protect those
inputs from callers that are not allowed to choose the Fleet distribution
trust root. This feature does not claim same-user process isolation.

## Signed Release Contract

The descriptor envelope uses strict UTF-8 JSON:

```json
{
  "schema": "rusty.fleet.release_descriptor_envelope.v1",
  "payload_base64url": "<canonical-base64url>",
  "signature_base64url": "<canonical-base64url>",
  "signer_spki_sha256": "<64-lowercase-hex>"
}
```

The signature is RSA-PSS with SHA-256 over the exact decoded payload bytes.
The signer SPKI digest must match both the envelope and configured pin. The
signed payload is also strict:

```json
{
  "schema": "rusty.fleet.windows_release.v1",
  "descriptor_id": "<release-id>",
  "product": "rusty-fleet",
  "version": "1.2.3",
  "channel": "stable",
  "issued_at_ms": 1800000000000,
  "expires_at_ms": 1800086400000,
  "asset": {
    "name": "RustyFleet-Setup.exe",
    "size_bytes": 123456,
    "sha256": "<64-lowercase-hex>",
    "signer_certificate_sha256": "<64-lowercase-hex>",
    "media_type": "application/vnd.microsoft.portable-executable",
    "installer_protocol": "rusty.fleet.guided_setup.v1"
  }
}
```

Unknown or duplicate properties, case variants, noncanonical base64url,
malformed three-part versions, an issue time more than 30 seconds in the
future, expiry, validity longer than fourteen days, wrong
product/channel/asset/protocol, oversized inputs, and signer mismatch are
rejected. The descriptor is capped at 64 KiB and the asset at 512 MiB.

The HTTPS client does not follow redirects automatically. GitHub Release
downloads may make one HTTPS redirect from `github.com` to
`release-assets.githubusercontent.com`; any other or chained redirect is
rejected. GitHub Pages downloads do not gain a redirect exception. Both the
stream bound and the descriptor's exact byte count are enforced.

## Verified Handoff Sequence

1. Verify the trust policy and signed, fresh descriptor.
2. Open the local state root through non-reparse Windows handles and acquire an
   exclusive owner lock.
3. Reject a consumed descriptor ID or a version below the highest prior
   verified handoff. A different descriptor at the same version is also
   rejected; a corrected/reissued installer must advance the version.
4. Create a unique operation-owned directory and create the exact installer
   file without overwrite.
5. Stream with a hard byte bound, flush it, and verify exact size and SHA-256.
6. Retain a read-only file handle so the verified executable cannot be replaced
   or opened for writing during trust verification and launch.
7. Ask Windows `WinVerifyTrust` to validate Authenticode, then match the exact
   signer-certificate digest to both the signed descriptor and configured pin.
8. Start the exact staged file with only `--plan --json`, an allowlisted
   environment, the private stage as its working directory, bounded output,
   and a 30-second timeout. Require strict schema
   `rusty.fleet.guided_installer_plan.v1` bound to product, version, channel,
   asset SHA-256, and `ready=true`.
9. Flush a create-new state file and commit its same-directory replacement with
   Windows write-through semantics before opening the installer. This consumes
   the descriptor and raises the strictly monotonic handoff version. A crash or
   failed guided run cannot silently replay an already admitted release.
10. Start the same exact file with no arguments, no shell, no `runas` verb, no
    hidden elevation, and a 15-minute timeout. A nonzero exit or standard-error
    output fails the handoff.
11. Record the sanitized outcome and delete only the operation-owned file and
    directory through their validated handles. Each launch is assigned to a
    Windows Job Object; success requires that assigned job to become empty,
    while timeout, cancellation, or bounded-output failure terminates the job
    before cleanup.

The guided installer itself may present a normal visible Windows consent flow
that Fleet owns. File Manager neither manufactures nor suppresses it.
Job containment is cleanup and timeout defense for the already
descriptor/hash/Authenticode-pinned trusted installer, not a sandbox for
malicious trusted code. `ProcessStartInfo.ArgumentList` starts the process
before Windows permits File Manager to assign it to the Job Object, so the
small start-to-assignment interval is outside the job guarantee. A deployment
that does not trust the pinned Fleet publisher must not enable this handoff.

## Receipts

Status uses schema
`questionable.file_manager.fleet_installer_status.v1`; a completed handoff uses
`questionable.file_manager.fleet_installer_handoff.v1`. Receipts include only
the product, version, channel, descriptor ID/digest, signer digests, asset
size/digest, plan/guided/cleanup facts, sanitized outcome, and observation
time. They never contain:

- descriptor or redirect URLs;
- local configuration, staging, trust-key, or executable paths;
- process arguments, environment, or raw standard output/error;
- credentials, tokens, keys, or certificates;
- device identifiers, ADB state, network endpoints, or hotspot state.

`guided_installer_completed` means the verified Fleet-owned process returned
successfully and private staging was cleaned. It is not a File Manager claim
about device enrollment, Fleet service health, or any later Fleet operation.

## Offline Acceptance

Core tests generate ephemeral RSA-signed descriptors and in-memory installer
fixtures without network or device access. Positive and negative coverage
includes:

- exact WPF/CLI typed route parity and confirmation;
- strict, duplicate, and unknown JSON fields;
- descriptor signature/SPKI pin and installer signer pin;
- product, channel, version, asset name/media/protocol bindings;
- exact asset size and SHA-256;
- future, expired, and overlong descriptor validity;
- replay and downgrade across service instances;
- unreviewed and chained redirects;
- installer-plan mismatch;
- guided timeout, process-tree termination, and private-stage cleanup;
- workspace reparse rejection.

These tests verify the distribution boundary only. They do not replace Fleet's
own guided-installer tests or a signed Fleet release-publisher gate.
