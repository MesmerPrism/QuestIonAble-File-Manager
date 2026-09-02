# Inspected single-device deployment

These routes are a bounded host/operator slice, not a general ADB, shell,
intent, component, or MCP execution surface.

`apk inspect --file <path-to.apk>` uses Android SDK Build Tools (`aapt2` and
`apksigner`) to record file size/SHA-256 and exact package, version, signer, and
split facts. Empty, malformed, ambiguous, multi-signer, and standalone split
inputs fail closed. Direct CLI routes first copy the source into one
identity-bound, non-replaceable admission workspace; hashing, Build Tools, and
ADB receive only that retained copy. The fixed workspace is single-owner,
bounded, and cleans any prior crash residue before reuse.

`apk install` repeats inspection immediately before its serial-scoped install.
It then reads package paths from that exact serial and streams the opened
installed base APK through a hard byte bound without creating a host copy.
Confirmation requires exact streamed base-APK SHA-256/size equality; only then
is the already inspected package/version/signer identity projected as installed.
ADB exit status alone is not confirmation. The receipt identifies the selected
serial plus both expected and installed byte evidence.

`apk preflight --serial <quest-serial> --file <path-to.apk>` is the read-only
agent handoff before deployment. It admits one immutable base APK, reports its
min/target SDK and declared launcher facts, selects exactly one discovered ADB
serial, compares the device API to minSdk, and classifies installed base bytes
as absent, exact, different, or unverified. Only exact installed bytes trigger
the existing read-only unique/exported launcher proof. The result separately
reports readiness for fixed deploy, launch, and diagnostic routes and returns
their argument arrays. It never installs, starts an activity, captures logs, or
invokes a project build. Its manifest inspection requires exactly one positive
numeric minimum-SDK Build Tools fact; missing, malformed, duplicated, or
conflicting values fail preflight rather than becoming a default.

`apk deploy --serial <quest-serial> --file <path-to.apk>` composes the common
single-base-APK agent loop without opening a generic execution surface. One
immutable admission remains alive across install, exact installed-byte
readback, launcher resolution/export proof, fixed component dispatch, and final
runtime observation. Installed bytes are checked after Package Manager to gate
launch, then checked again before final runtime probes. The command
returns one `questionable.file_manager.apk_deploy_result.v1` JSON envelope; its
result carries `questionable.file_manager.apk_deployment.v3` install, launch,
runtime evidence, and a structural QFM claim boundary plus the overall mutation
receipt. `confirmed` applies only to QFM's exact-installed-byte and resolved
component launch effect. It never means application readiness, OpenXR
readiness, app effect, or wearer visibility. Sanitized failure
envelopes conservatively report whether a device state change may have occurred.
Callers still cannot provide package names, components, intents, extras, shell
fragments, or generic ADB arguments.

`apk launch --serial <quest-serial> --file <path-to.apk>` derives its package
only from the inspected APK, requires matching installed identity, queries the
installed base digest, and size before querying the fixed exported
`MAIN`/`LAUNCHER` surface. It requires exactly one safe component and starts
that exact queried result only after a same-package export proof. A matching
`ActivityInfo`/`Activity` detail record is authoritative and must contain
exactly one `exported=true` value. When current Quest package dumps omit those
detail records, the fallback accepts only the exact queried component appearing
exactly once beneath `Activity Resolver Table` -> `Non-Data Actions` ->
`android.intent.action.MAIN`, with that same filter declaring both the exact
`MAIN` action and `LAUNCHER` category. Shorthand and full same-package class
names normalize to one component. An activity alias is allowed only when that
single queried component has one of those export proofs and the same proof
retains both `isAlias=true` and one syntactically safe `targetActivity`; the
CLI still dispatches the queried alias, never the target. The launch result
adds `launcherIsActivityAlias` and canonical `launcherTargetActivity` facts so
consumers can distinguish that case. Those are additive launch-result properties:
they do not rename, remove, or reinterpret an existing launch field or any
advertised v2 contract ID, so existing tolerant v2 readers continue to consume
their contracted facts. Cross-package, alias/substitution without both facts,
ambiguous, explicitly unexported, malformed, or incomplete evidence fails before
dispatch. Callers cannot supply components, actions, categories,
intents, extras, paths, shell fragments, or generic ADB arguments. Confirmation
requires exact canonical resumed-component readback for the dispatched alias or
its retained alias target.

`apk launch ... --json` writes exactly one
`questionable.file_manager.apk_launch_result.v1` document to standard output on
success or failure. Failure documents contain a stable sanitized reason and
whether fixed-component dispatch was attempted; they do not mirror command
output to standard error or claim successful dispatch after `am start` fails.

`apk observe --serial <quest-serial> --file <path-to.apk>` returns matching
installed package and byte facts, foreground/top-resumed flags, exact observed
foreground and top-resumed component sets, known blocking Quest system
components, package process IDs, and the separately parsed global Android
`mCurrentFocus` and `mFocusedApp` facts from a fixed serial-scoped
`dumpsys window windows` readback. The component sets and global focus facts
are independent: an immersive app can be top-resumed and alive while global
focus is a Meta FocusPlaceholderActivity, or the target can be globally focused
while `pidof` is empty. QFM uses a fixed byte-limited source stream and bounded
parser before retaining each global focus field's structured components, record
count, source metadata, and reported/absent/empty/malformed/unknown/unavailable
state; it never publishes its raw WindowManager dump.

Runtime observation v5 retains the prior v4 single-component `currentFocus` and
`focusedApp` projections for compatibility, alongside the richer `globalFocus`
aggregate; consumers needing the bounded multiple-record detail must use the v5
aggregate.

Multiple or stale-looking records remain visible as records rather than being
collapsed into a handoff result. FocusPlaceholderActivity is an observed system
component, not a universal failure verdict. Consumers choose their own 2D or XR
acceptance policy; File Manager does not infer OpenXR readiness. Identity,
digest, and size must match before runtime probes execute. It does not claim
effective in-app settings, app effect, or wearer-visible state. Process IDs and
legacy resumed text prove neither XR readiness nor an app effect. `pidof`
quality is explicit: only a clean zero-PID readback is reported as no process;
unusable or nonzero `pidof` output is an observation limitation, not a negative
readiness classification.

`apk diagnose --serial <quest-serial> --file <path-to.apk> --output
<new-folder>` is a read-only durable projection of that same exact-artifact
boundary. It refuses an existing output path, stages the complete result in a
new sibling directory, and atomically publishes the directory only after its
manifest is complete. The fixed capture set is documented in
`apk-diagnostic-bundle.md`; callers cannot supply commands, tags, PIDs, log
counts, or capture kinds.

`apk uninstall --serial <quest-serial> --file <path-to.apk>
--confirm-exact-apk-uninstall --json` is the destructive cleanup companion. It
holds an immutable inspected APK, requires one exact matching installed base
APK and one ready serial immediately before its single derived-package
uninstall, and then reads both fixed unscoped and current-user package absence.
Installed split sets, absent/different/unverified preimages, arbitrary package
or user arguments, retries, and WPF/Local API projections are rejected. The
route removes the app and may delete app-private data. `confirmed` proves only
the two reported absence scopes; a caller needs a separately authenticated
absent pre-run snapshot and run-owned install before calling that restoration.
Dispatch or readback ambiguity remains `cleanupUnknown` in the mutation receipt.
`apk properties observe|clear|restore` extends exact-artifact identity without
extending deployment or readiness claims. It consumes one complete
`rusty.quest.android_property_manifest.v1` owned by the APK package. Observe
publishes schema `questionable.file_manager.apk_property_snapshot.v1` through
one no-overwrite local file. Public result envelopes are
`questionable.file_manager.apk_property_observation_result.v1` and
`questionable.file_manager.apk_property_mutation_result.v1`. Clear requires
the live preimage to equal the snapshot; restore consumes the snapshot as the
only value source. Successful mutation receipts bind exact property and
installed-base-APK readback. These facts do not prove application readiness,
OpenXR readiness, app effect, or wearer visibility.

`operator-actions --json` advertises the preflight-result,
inspected-deployment, launch-result, deploy-result, diagnostic-result,
launcher-export-proof, and runtime-observation contract revisions without
selecting a device or performing a mutation. The consolidated
`questionable.file_manager.inspected_deployment.v5` contract requires all of
the behavior above: immutable artifact admission, exact installed-byte
readback, one JSON launch envelope on success or failure, the bounded current-
Quest resolver-table fallback, and runtime observation v5. Provider resolvers
should require these exact revisions before a run so an older hash-pinned CLI
cannot silently reintroduce empty launch JSON or reject the known Quest VR
launcher projection.

`tests/QuestIonAbleFileManager.Core.Tests/Fixtures/inspected-deployment-provider-conformance.v1.json`
is the public synthetic corpus for host consumers. Its
`questionable.file_manager.inspected_deployment_provider_conformance.v1`
metadata fixes the four native schema IDs, the launch-envelope nullability
invariant, transport adversarial cases, and the runtime-v5 proof boundary. It
states explicitly that runtime observation v5 proves Android installed,
foreground, top-resumed, process, and global-focus dimensions only—not
panel-paused state, advancing focused/submitted frames, the >=750 ms stability
window, app-owned handoff markers, OpenXR readiness, app effect, or wearer
visibility. QFM reports Android focus observations only; the application owns
all interpretation. A consumer owns its terminal envelope and final file
behavior; this corpus does not replace a native File Manager schema.

This slice requires Android Platform Tools and Android SDK Build Tools. It is
single-device and single-base-APK only. Split-set inspected deployment and
app-owned effective-state attestation remain out of scope.

The optional dedicated local API projects only these four inspected-deployment
routes through retained typed commands. See `local-api.md`.
The preflight and composite deploy convenience routes are CLI-only in v1;
local API clients retain the existing inspect/install/launch/observe
primitives.
