# Codex Usage Notch

A small Windows tray app that shows the Codex allowance included with your ChatGPT plan. Hover the tray icon or the top-of-screen strip to see remaining usage and reset times.

![Dark usage card with sample values](Assets/usage-card-dark.png)
![Light usage card with sample values](Assets/usage-card-light.png)

## Run

Requires Windows x64, an interactive desktop, and an installed Codex executable. Choose one edition:

| Executable | Requirements |
| --- | --- |
| `CodexUsageNotch.exe` | Self-contained; includes .NET |
| `CodexUsageNotch-lite.exe` | Requires the .NET 8 Windows Desktop Runtime |

Release builds put both executables in the project root. They are ignored by Git; a fresh checkout needs to be built first. Run one edition at a time; a second instance exits quietly.

The app finds `codex.exe` beside itself, in conventional Codex installation folders, on PATH, or in the ChatGPT VS Code extension. It uses your existing Codex login. **Connect ChatGPT** opens browser sign-in when needed; closing the sign-in window cancels the attempt. Attempts expire after five minutes.

## Controls

| Action | Result |
| --- | --- |
| Hover the tray icon | Preview after a 500 ms delay following Windows' hover notification; moving away cancels it |
| Click the tray icon or press Enter/Space on it | Immediately open the interactive card |
| Hover the top strip for 850 ms | Show the usage preview |
| Move into the preview | Keep it open for reading |
| Leave the icon and preview | Close after a 400 ms grace period |
| Press Escape or click elsewhere | Close the interactive card |
| Right-click the tray icon | Refresh, connect, toggle the strip or usage ring, configure startup, or exit |

The app follows Windows light/dark mode, high contrast, display DPI, and text size. The strip stays at the top of the primary monitor. Strip visibility and the optional tray ring are remembered. **Start with Windows** is opt-in; keep the executable in a stable location when enabled.

## Usage, updates and privacy

Values represent subscription allowance, not API spending or token counts. Mint means more than 25% remaining, amber means 11–25%, and red means 10% or less. The card shows the window durations and reset times reported by Codex. An expired reset displays **Awaiting reset update** rather than assuming the allowance has replenished.

The app owns one local `codex app-server --listen stdio://` subprocess. It requests usage once per minute, receives usage notifications, and refreshes immediately on request. Local countdown repainting sends no requests. Requests time out after 15 seconds; connection failures retry after 5, 15, 30, then 60 seconds. Failed refreshes or readings older than two minutes show **LAST KNOWN**.

Authentication stays with Codex. The indicator does not store credentials, account identity, or usage history. Preferences live in `%LocalAppData%\CodexUsageNotch\settings.json`. Sanitized `diagnostics.log` and `diagnostics.log.1` files in the same folder record operation names, timestamps, exception types, and error codes, with roughly 128 KiB per file. They exclude raw responses, sign-in URLs, and credentials. The indicator uploads no logs.

## Develop and test

Requires the .NET 8 SDK or newer. The project targets `net8.0-windows` and has no third-party packages. Initial restore may download Microsoft build/runtime packs. Run commands from the project root:

```powershell
# Build and run locally. Exit any existing instance first to see the new build.
dotnet run --project CodexUsageNotch.csproj -c Release

# Core tests only: no desktop interaction or Codex login needed.
.\build\test.bat -CoreOnly

# Desktop checks: briefly move and then restore the pointer.
.\build\test.bat

# Optional read-only check of your installed, signed-in Codex connection.
dotnet run --project tests\CodexUsageNotch.Tests.csproj -c Release -- --live
```

Tests use a console runner, so use `dotnet run`, not `dotnet test`. Failed checks return a nonzero exit code. Core tests cover parsing and connection behavior; desktop checks cover tray, popup, hover, and sign-in interactions. Builds never run tests. The test script compiles and runs the tests without publishing or replacing releases. Add `-Smoke` to also check both existing root release executables; it does not rebuild them. Successful smoke reports are removed; failed reports remain for diagnosis.

For UI layout work, generate 324 sample images and bounds/overlap checks across three palettes, three DPI scales, three text sizes, and twelve states:

```powershell
dotnet run --project CodexUsageNotch.csproj -c Release -- --render-previews .\build\previews
```

## Build a release

Exit the indicator, then run:

```powershell
.\build\build-release.bat
```

This publishes both editions, verifies their hashes, and replaces the root executables. **No tests run during a build**, including desktop or smoke tests. Successful promotion automatically deletes staging and its temporary build manifest. Failed builds retain output for diagnosis. No `.previous` backups or duplicate `dist/` releases are created.

To build while the indicator is running, use `build-release.bat -StageOnly`, exit the app when staging finishes, then use `build-release.bat -PromoteOnly`. Start your preferred root executable after promotion. A normal `dotnet build` does not update these release executables.

## Clean generated files

```powershell
.\build\clean.bat -WhatIf # Show what would be removed.
.\build\clean.bat         # Remove generated files.
```

Cleanup removes `bin/`, `obj/`, their test equivalents, staging files, preview images, smoke reports, old protocol-review output, test results, the obsolete `dist/` folder, and root `.previous`/`.new` leftovers. It preserves source, Git history, root release executables, and application settings. **Pending staged releases are removed too**; finish promotion first if you need them. `-StagingOnly` removes just staging.

The scripts in `build/` handle building (`build-release.bat`), testing (`test.bat`), and cleanup (`clean.bat`), each backed by a PowerShell script. The `.bat` launchers avoid unsigned-script restrictions for their child process without changing the machine execution policy. Build caches remain after a release to speed up subsequent builds; run cleanup when you want a tidy workspace.

## Source layout

| Path | Responsibility |
| --- | --- |
| `Program.cs` | Startup, single-instance guard, preview and smoke modes |
| `NotchApplicationContext.cs` | Coordinates refreshes, sign-in, tray actions, and shutdown |
| `Core/` | Usage parsing/state, preferences, startup registration, diagnostics |
| `Integration/` | Codex discovery and app-server transport; Windows tray integration |
| `UI/` | Cards, windows, themes, icons, and preview rendering |
| `Assets/` | App icons and sample screenshots |
| `tests/` | Core and desktop test runners |
| `build/` | Build, test, and cleanup scripts |

## Troubleshooting

- **Codex not found:** install Codex or expose its underlying Windows `codex.exe` on PATH, then Refresh. An npm `.cmd` launcher alone is insufficient.
- **Last known usage:** check Codex's connection and select Refresh. The previous reading remains visible during retries.
- **Sign-in failed:** select Connect ChatGPT again to request a fresh link.
- **Tray icon hidden:** check Windows' notification overflow area.
- **Changes not visible:** exit the existing instance and run the new build; root releases need the release script.
- **Cleanup reports a running app or locked files:** exit the development/test executable and retry.

The app-server interface can change with Codex updates. Mixed-DPI monitors, Narrator, overflow-tray behavior, Explorer restart, sleep/resume, and browser/network failures still need manual checks on target hardware. There is no installer, signing, automatic updater, or public release pipeline.

To uninstall, disable **Start with Windows**, exit, and delete the executable. Delete the local settings/log folder separately if desired.
