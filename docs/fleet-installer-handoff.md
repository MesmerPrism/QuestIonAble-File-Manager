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

## Release-Owned Configuration

A published File Manager release receives its Fleet consumer trust boundary at
build time from the complete metadata block in checked-in
`src/QuestIonAbleFileManager.Core/FleetInstallerReleaseConfiguration.cs`:

| Checked-in metadata field | Meaning |
| --- | --- |
| `ConfigurationVersion` | Exact `2` |
| `DescriptorUri` | Exact `https://mesmerprism.com/Rusty-Fleet/metadata/<channel>/release.json` |
| `DescriptorPublicKeySpkiBase64` | Canonical base64 DER RSA SubjectPublicKeyInfo; public verification material, never a private key |
| `DescriptorSignerSpkiSha256` | Lowercase SHA-256 of that exact SPKI |
| `InstallerSignerCertificateSha256` | Lowercase SHA-256 of the exact DER Windows Setup signer certificate |
| `ProvisioningSetupSignerCertificateSha256` | Lowercase SHA-256 of the reviewed QFM Setup signer certificate allowed to provision/repair protected machine replay state |
| `Channel` | Exact `stable`, `preview`, or `dev` channel |
| `StateRootRelativePath` | One-to-four-segment safe relative path under the current user's Local Application Data |

The reviewed source block has this exact shape:

```csharp
using System.Reflection;

[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.ConfigurationVersion", "2")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.DescriptorUri", "https://mesmerprism.com/Rusty-Fleet/metadata/<channel>/release.json")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.DescriptorPublicKeySpkiBase64", "<canonical-public-rsa-spki>")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.DescriptorSignerSpkiSha256", "<lowercase-spki-sha256>")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.InstallerSignerCertificateSha256", "<lowercase-setup-certificate-sha256>")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.ProvisioningSetupSignerCertificateSha256", "<lowercase-qfm-setup-certificate-sha256>")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.Channel", "<channel>")]
[assembly: AssemblyMetadata("QuestIonAbleFileManager.FleetInstaller.StateRootRelativePath", "QuestIonAbleFileManager/FleetInstaller")]
```

The default checked-in file is intentionally inert. Enabling the handoff is an
intentional source change containing all eight public fields in the release
commit. The official release gate requires the exact clean commit to carry the
matching `v<version>` tag, compiles and validates the complete metadata, and
then enters the existing Windows signing pipeline. Ordinary MSBuild
properties, environment variables, release-script arguments, and generated
`obj` files are not inputs to this trust configuration. The gate builds with
the former six ambient property names and proves that the compiled metadata is
byte-for-byte unchanged. Custom source builds can of course edit checked-in
source, but they cannot become an official signed QFM release.

No private key, certificate, absolute state path, installer binary, or source
credential belongs in the checked-in configuration. The public descriptor key
and public signer digests do.

Embedded configuration is authoritative. When it exists, File Manager does not
read the developer environment variables below, so another same-user process
cannot redirect a published release by changing them. A release built without
embedded configuration is inert unless deliberately configured as a
development build; status reports `not_configured`.

## Development And Offline Configuration

Source builds retain an explicit environment path for development and offline
tests:

| Environment variable | Meaning |
| --- | --- |
| `QUESTIONABLE_FILE_MANAGER_FLEET_RELEASE_DESCRIPTOR` | The exact canonical MesmerPrism Pages metadata URI |
| `QUESTIONABLE_FILE_MANAGER_FLEET_INSTALLER_STATE` | Local private state/staging directory |
| `QUESTIONABLE_FILE_MANAGER_FLEET_DESCRIPTOR_PUBLIC_KEY` | PEM file containing the trusted RSA descriptor public key |
| `QUESTIONABLE_FILE_MANAGER_FLEET_DESCRIPTOR_SIGNER_SHA256` | Lowercase SHA-256 of that key's DER SubjectPublicKeyInfo |
| `QUESTIONABLE_FILE_MANAGER_FLEET_INSTALLER_SIGNER_SHA256` | Lowercase SHA-256 of the exact DER Windows signer certificate |
| `QUESTIONABLE_FILE_MANAGER_FLEET_CHANNEL` | Pinned descriptor channel; defaults to `stable` |

A partial or invalid configuration fails closed. An absolute local descriptor
path is accepted only when
`QUESTIONABLE_FILE_MANAGER_FLEET_ALLOW_LOCAL_FIXTURE=1`; its sibling
`RustyFleet-Setup.exe` is test input only. The status receipt distinguishes
`embedded_pages_metadata`, `environment_pages_metadata`,
`environment_local_fixture`, and inert `none` source kinds without exposing a
URL or path.

The production source split is fixed: MesmerPrism Pages carries only the small
signed `release.json` metadata document. It must not carry, mirror, redirect
to, or place `RustyFleet-Setup.exe` beside that document. The signed payload
instead binds the exact immutable GitHub Release asset URL.

## Signed Release Contract

The descriptor envelope uses strict UTF-8 JSON:

```json
{
  "schema": "rusty.fleet.release_descriptor_envelope.v2",
  "payload_base64url": "<canonical-base64url>",
  "signature_base64url": "<canonical-base64url>",
  "signer_spki_sha256": "<64-lowercase-hex>"
}
```

The signature is RSA-PSS with SHA-256 over the exact decoded payload bytes.
Those bytes must also be the RFC 8785 JSON Canonicalization Scheme (JCS)
serialization: lexicographically sorted object names, no insignificant
whitespace, shortest required string escaping, UTF-8, and I-JSON safe
integers. Verifying a signature is not enough if the signed bytes deserialize
to the same object through another ordering, escape, whitespace, or number
spelling. The signer SPKI digest must match both the envelope and configured
pin. The signed payload fields are shown expanded below for readability; the
actual signed bytes are the compact JCS form with this lexicographic order:

```json
{
  "asset": {
    "installer_protocol": "rusty.fleet.guided_setup.v1",
    "media_type": "application/vnd.microsoft.portable-executable",
    "name": "RustyFleet-Setup.exe",
    "sha256": "<64-lowercase-hex>",
    "signer_certificate_sha256": "<64-lowercase-hex>",
    "size_bytes": 123456,
    "url": "https://github.com/MesmerPrism/rusty-fleet/releases/download/v1.2.3/RustyFleet-Setup.exe"
  },
  "channel": "stable",
  "descriptor_id": "<release-id>",
  "expires_at_ms": 1800086400000,
  "issued_at_ms": 1800000000000,
  "product": "rusty-fleet",
  "schema": "rusty.fleet.windows_release.v2",
  "validity_duration_ms": 86400000,
  "version": "1.2.3"
}
```

Unknown or duplicate properties, v1 schemas, non-JCS signed payload bytes,
case variants, noncanonical base64url, malformed three-part versions, an issue
time more than 30 seconds in the future, expiry, a missing/nonpositive validity,
validity longer than 24 hours, or any value where `expires_at_ms` is not
exactly `issued_at_ms + validity_duration_ms`, wrong
product/channel/asset/protocol, an asset URL whose exact numeric `v<version>`
tag differs from the payload, oversized inputs, and signer mismatch are
rejected. The descriptor is capped at 64 KiB and the asset at 512 MiB.

The HTTPS client does not follow redirects automatically. The canonical Pages
metadata request may not redirect. The explicit GitHub Release asset may make
one HTTPS redirect from `github.com` to
`release-assets.githubusercontent.com`; any other or chained redirect is
rejected. Both the stream bound and the descriptor's exact byte count are
enforced.

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
9. Start the same exact file with no arguments, no shell, no `runas` verb, no
   hidden elevation, no redirected streams, and a 15-minute timeout. The
   console-guided prompt is genuinely visible. A nonzero exit fails the
   handoff.
10. After a zero exit, read the clock again and require that the exact
    descriptor is still fresh. Only then consume it, raise the monotonic
    handoff version, and commit state with Windows write-through semantics. A
    declined, timed-out, or failed guided run records only a sanitized failed
    outcome. A descriptor that expired while the visible prompt was open
    remains unconsumed and requires a fresh metadata fetch before retry. This
    does not admit a lower version or a different same-version descriptor.
11. Delete only the operation-owned file and
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

Replay state is local defense in depth, not a permanent external ledger.
Elevated signed QFM Setup independently verifies its own Authenticode chain and
reviewed signer pin, installs a same-signed protected replay-authority copy
under Program Files, writes an empty state plus root-bound sibling anchor, then
creates an HKLM machine record whose protected ACL grants write only to SYSTEM
and Administrators and read to Users. The machine record owns the accepted
descriptor IDs and monotonic version high-water mark; the mutable per-user
files do not. Core cannot write the record. After guided success it may invoke
only the installed helper's fixed accept route; the elevated helper verifies
its own Authenticode and pinned signer, re-fetches and independently verifies
the current signed descriptor, requires an exact request match, and applies
only a monotonic transition. It holds a machine-wide named mutex, protected by
a SYSTEM/Administrators-only DACL that is revalidated on every open, from
before descriptor refetch through record read/check/write and exact durable
readback. Initial provisioning and explicit repair hold the same lock, so a
stale concurrent Setup cannot replace accepted machine state with an empty
record. If a helper exits while holding the mutex, its successor treats the
lock as abandoned but re-reads protected state before deciding. Loss of either user file—or
coordinated deletion of both—fails closed while the machine record remains.
Loss of the machine record also fails with
`fleet_installer_recovery_required`; absence is never interpreted by runtime
as a fresh root.

Ordinary same-user code cannot provision, reset, rewrite, or roll back the
protected machine state. It can invoke the official helper, but cannot make
that helper accept unsigned, mismatched, replayed, equal-version, or downgraded
state. Signed QFM Setup with the
explicit `--repair-fleet-replay-protection` option is the deliberate
recovery/reset boundary; an ordinary reinstall refuses a lifecycle reset. The
writer and reset implementation exists only in Setup, not in Core, and Setup's
machine-readable result records whether it provisioned, preserved, repaired,
or reset the machine record/files without exposing the state path. Paired
replay files are validated before preserving or repairing the machine record,
and a partial pair is refused for repair. Status and install otherwise fail
closed, including for the same descriptor or a lower version.
Release acceptance covers a two-process same-descriptor race (exactly one
success), ordered different-version contention (no rollback), and helper-exit
abandoned-lock recovery. Setup additionally uses a fresh unpredictable staging
directory under its protected Program Files product root while elevated,
rejects reparse components, and deletes that run directory afterward. Plan-only
staging is unelevated and per-run. Quiet output is bounded and contains no
local staging path on success or failure.
The publisher must still issue descriptors for no more than 24 hours, keep the
signing key and release channel controlled, and advance the release/version
when republishing. The verifier admits an issue time exactly 30 seconds ahead,
rejects 30 seconds plus one millisecond, requires the signed duration to bind
the two timestamps, admits an exact 24-hour validity interval, and rejects at
`expires_at_ms` itself.

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
- exact JCS bytes, including rejection of reordered, whitespace-varied,
  unnecessarily escaped, and alternate numeric forms;
- v1 envelope/payload rejection and exact v2 schema binding;
- descriptor signature/SPKI pin and installer signer pin;
- product, channel, version, asset name/media/protocol and immutable URL/tag
  bindings;
- exact asset size and SHA-256;
- exact 30-second future-skew, required duration/timestamp relation, 24-hour
  lifetime, post-prompt freshness, and exclusive-expiry boundaries;
- replay and downgrade across service instances, fail-closed single and
  coordinated replay-file deletion, protected machine-record loss, and
  lower-version replay after state deletion;
- Pages-sibling binary rejection and unreviewed, escaping, and chained
  redirects;
- embedded-over-environment precedence, incomplete/unknown configuration, and
  unsafe per-user state paths;
- checked-in release-source authority, exact clean-tag gate, and inertness of
  all former ambient MSBuild trust properties;
- receipt exclusion of source URLs and private paths;
- installer-plan mismatch;
- visible guided launch, retry after decline/timeout, descriptor consumption
  only after success, process-tree termination, and private-stage cleanup;
- workspace reparse rejection.

These tests verify the distribution boundary only. They do not replace Fleet's
own guided-installer tests or a signed Fleet release-publisher gate.
