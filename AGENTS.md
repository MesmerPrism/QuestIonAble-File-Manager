# QuestIonAble File Manager Agent Notes

This is public MIT-licensed portable source for a Windows-first operator tool
for ADB-authorized Meta Quest headsets. File Manager owns typed file/APK,
diagnostic, Wi-Fi ADB, reviewed power and local deployment operations and
Windows delivery. Kiosk remains a separate AGPL Android product. Fleet owns
managed orchestration and policy; Manifold owns accepted authority.
Normal File Manager features work without Kiosk or optional Fleet integration.

## Select the operation and contract

Start with README and the nearest relevant owner instructions. Detailed
provider, safety, CLI and release rules are retained in
[conditional operator contracts](docs/agent-operation-contracts.md).
Read the applicable section and focused guide rather than every provider or
release recipe for an unrelated task.

| Work | Focused guide |
| --- | --- |
| Typed command inventory and GUI/CLI parity | `docs/operator-cli-parity.md`; code-owned `OperatorActionRegistry` |
| Exact APK inspect/deploy/install/launch/readback | `docs/inspected-deployment.md`; `docs/agent-quest-apk-workflow.md` |
| Read-only diagnostic bundle | `docs/apk-diagnostic-bundle.md` |
| Wi-Fi ADB and bounded multi-target install | `docs/wifi-adb-and-parallel-install.md` |
| Optional local inspected-deployment API | `docs/local-api.md` |
| Fleet providers, profiles, discovery or installer handoff | Applicable Device Safety/Architecture section in the conditional contracts; `docs/fleet-integration.md`; `docs/fleet-installer-handoff.md` |
| Signing, packaging, release and update continuity | `docs/release-workflow.md`; conditional Release Posture and Build And Validation sections |

## Implementation and effect invariants

GUI and CLI invoke the same immutable typed `OperatorCommand` or
`KioskDirectOperatorCommand` arguments. Classify local-only UI actions honestly.
Keep business logic in Core, process execution behind `ICommandRunner`,
cancellation/timeouts bounded, and use `ProcessStartInfo.ArgumentList`
instead of a host shell. Cover every WPF operation with parity tests.
Agent automation uses the CLI, not WPF scraping. CLI JSON has one final result;
WPF progress uses `OperatorProgress` and indeterminate state without an honest
total. Do not infer percentage from console volume or elapsed time.

Select one exact ready serial and use serial-scoped device operations.
Read-only identity/readback comes before mutation. Every state-changing route
emits `OperatorMutationReceipt`: sent, pending, then confirmed only after
fresh operation-specific headset readback. Admission, exit zero and permission
prompts do not confirm effects; pending/timeouts remain reconcilable.

Continue within existing user/session authorization. Protected wearer approvals
remain wearer decisions. Preserve unrelated device/app state and the requested
final state. Disruptive daemon work, new delete/uninstall/clear-data surfaces,
and widened provider authority need their separate owner safety/UX route.
Use existing reviewed exact-inspected-APK cleanup, closed property and
launch-diagnose exceptions only under their detailed contracts; none is
generic shell or caller-selected command authority. Coordinate exclusive
headset, long build, disruptive ADB lifecycle and shared-port work as applicable.

APK bytes, package/version/signer, output identity and selected target must
stay bound to the chosen verification policy. Default deploy/launch uses exact
installed identity. Explicit development-metadata confirmation proves stable
metadata and accepted install only, never installed bytes/signer/unique build
or readiness. Single-APK export rejects splits; complete split-set install
uses one deterministic atomic package set. APK copies do not include data,
OBBs, assets or Store entitlement.

Optional providers remain closed and separate: discovery is inert description,
never activation, backend health, target or execution authority. Fleet Core
push requires injected current Quest/Manifold authority and remains one-use,
one-target, bounded/no-overwrite. Catalog endpoints/credentials and connectivity
profiles remain File Manager-owned; Fleet receives only the declared sanitized
evidence. Dedicated Fleet providers use exact pinned self-contained artifacts
and private per-launch extraction, never the general CLI/apphost.
Kiosk Direct Link cleanup/recovery preserves ownership/generation and ambiguous
outcomes; lost credentials cannot be reconstructed. Read the conditional rules
before modifying these surfaces.

The local API is loopback-only, bearer-protected and inert until explicitly
started; its retained journal/anchor/state boundary is mandatory. Do not start
it during ordinary validation. Fleet installer handoff is distribution-only,
with complete checked-in trust configuration, exact signed metadata/assets,
protected replay authority and guided-install evidence. Handoff dispatch alone
does not prove installation. Detailed repair/rollback/rotation rules remain
in the owner contracts.

## Public boundary and validation

Keep serials, private packages, captures/logs, signing keys/certificates, local
absolute paths, downloaded tools and generated release artifacts out of public
commits. Use placeholders in docs. Third-party redistribution requires its
reviewed upstream terms. Never print/persist a PFX password.

Use PowerShell 7.6 or newer through `pwsh`. Select local checks by changed
surface. Documentation/instruction changes use `git diff --check`,
`pwsh -NoProfile -File tools/Test-PublicBoundary.ps1` and changed-link/command
verification. Core/CLI/WPF behavior uses Release build/solution tests and CLI
smoke; branding, provider artifacts and signed releases use the corresponding
focused gates listed in the conditional Build And Validation section.
CI still runs every required complete PR gate. Device validation and signed
release validation are separate scoped gates.

Published assets are immutable and require a new version for changes. Preserve
stable package identity, documented byte-identical former-name aliases and
independent product-channel, maturity and distribution-track axes. Labs remains
opt-in and co-installable with exact Labs roots/identities and owner metadata.
Private publisher inputs are never release assets or committed source.
Keep long recipes and rare provider contracts in their focused owner documents.