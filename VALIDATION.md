# Validation

## Repository cleanup — 1.2.2 (2026-09-16)

- Cleanup dry run and actual cleanup completed; release executables, staging and rollback copies were preserved.
- Release build succeeded from cleaned output with zero errors. NuGet emitted NU1900 warnings because the vulnerability-data endpoint at `api.nuget.org` was unavailable in the validation environment.
- All 40 current core regression checks passed using `dotnet run --project tests\CodexUsageNotch.Tests.csproj -c Release`.
- The current preview generator defines 324 cases: three palettes, three DPI scales, three text scales and twelve states. Desktop, preview, live-account and release-promotion checks were not rerun for this documentation and repository cleanup.

The results below describe the earlier 1.2.1 validation session, not new validation of 1.2.2.

## Historical validation — 1.2.1

### Completed

- Release compilation: zero warnings and errors.
- 25 core regression tests: full/partial snapshots, explicit nulls, invalid values, stale state, countdowns, settings compatibility, placement, reconnect backoff, handshake serialization, timeout teardown, cancellation, account changes, malformed notifications, EOF recovery, login responses/completion, insecure login URL rejection, pending-login cancellation, and obsolete connection generations.
- 18 desktop interaction checks on the available Windows desktop: top-center strip positioning on startup and hide/show without taking focus; native tray registration; version-4 hover/click/keyboard/menu message routing; non-activating preview; work-area clamping; pointer transition and close delay; interactive focus; accessible buttons; Refresh; Escape; loss-of-activation dismissal; and live palette changes.
- 144 rendered layout checks: all text rectangles remain inside the card and do not overlap at 100%, 150%, and 200% DPI, with normal and doubled text size in light, dark, and system high-contrast palettes. Includes 0%, 100%, stale, missing-window, long-reset, connecting, missing-Codex, and sign-in states. Light and dark samples visually inspected.
- Live read-only handshake and allowance request against the installed Codex app-server: both allowance windows returned; owned subprocess disposed afterward.
- Promoted app launch: one owned Codex subprocess; launching the other edition exits successfully without creating a duplicate instance.

## Release artifacts

`build\build-release.ps1` records executable hashes only after both staged editions pass their native tray/startup/shutdown smoke test. `dist\release-manifest.json` records the promoted artifacts. Build and smoke-test failures prevent promotion.

## Manual checks still needed on target hardware

Automated palette/layout and message tests do not establish every Windows Shell or hardware configuration. Before broader distribution, verify:

- Actual movement between monitors with different DPI, monitor removal/reconnection, and unusually small working areas with maximum text scaling.
- Actual Windows high-contrast and text-size changes with Narrator running, including the sign-in dialog and context menu.
- Natural Shell hover events and activation from the overflow tray on different Windows/taskbar configurations; the automated test injects version-4 notifications and tests pointer behavior separately.
- Explorer restart and sleep/resume. Recovery handlers are implemented; the development desktop was not restarted or suspended during validation.
- Browser login success, cancellation, authentication expiry, clipboard contention, and deliberately unavailable Codex/network conditions in a disposable Windows profile. Live account sign-in was not changed during testing.

No installer, code-signing, updater, or public-release infrastructure was tested or added.
