# Progress Reporting

## Decision

Show progress without claiming measurements that ADB does not expose. The WPF
footer uses one restrained progress bar beside the existing status text:

- indeterminate for discovery, directory/package listing, file transfer,
  single-device install, bundle install, export, and hashing;
- determinate for the five owned Wi-Fi enable phases; and
- determinate by completed target count for parallel installation.

The bar uses the existing warm-neutral palette and native WPF animation. It is
hidden when the operation finishes and does not add a dashboard, modal, badge,
or decorative loading surface.

## Authority And Interface

`OperatorProgress` is the shared optional progress contract. It contains a
stage identifier, human-readable message, completed units, and total units.
A total of zero explicitly means indeterminate. A positive total derives a
clamped percentage from owned work units.

`AdbClient` owns progress for multi-step and fan-out work. The
`OperatorCommandExecutor` forwards the optional `IProgress<OperatorProgress>`
adapter. WPF projects it into status text and the footer bar. UI handlers do
not inspect ADB output or invent parallel completion state.

The CLI command and result contracts do not change. In particular, `--json`
stdout remains one final machine-readable document instead of mixing transient
progress events into the result stream. Agents receive the same final
per-target evidence used by the GUI.

## Honest Units

| Operation | Indicator | Unit |
| --- | --- | --- |
| Wi-Fi enable and connect | Determinate | USB identity, address inspection, transport enablement, endpoint connection, network identity |
| Parallel APK install | Determinate | Headset targets completed |
| Parallel APK bundle install | Determinate | Headset targets completed |
| Other ADB operations | Indeterminate | No trustworthy total exposed |

File size, elapsed time, ADB console lines, and process output volume are not
used as percentages. An indeterminate indicator means work is active, not that
the app knows how much time remains.

## Validation

- Unit tests clamp determinate percentages and preserve an explicit
  indeterminate state.
- Wi-Fi tests require the exact `0, 1, 2, 3, 4, 5` phase sequence.
- Parallel tests require progress from zero through the exact target count,
  including a partial-failure run.
- Executor tests prove the optional shared progress adapter reaches core work.
- WPF build and visual smoke validate the footer projection, loading state,
  normal hidden state, and existing layout.

## Non-scope

- Fabricated byte, time-remaining, or APK-install percentages.
- Parsing unstable ADB prose as a progress protocol.
- A separate progress window, operation history dashboard, or notification
  center.
- Cancellation controls; cancellation remains a separate lifecycle feature.
