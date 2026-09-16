# Codex Usage Notch

A small native Windows indicator for the Codex allowance included with your ChatGPT plan. A quiet top strip shows your remaining allowance; hover the strip or tray icon for details, or click the tray icon for actions.

![Dark usage card with sample values](Assets/usage-card-dark.png)
![Light usage card with sample values](Assets/usage-card-light.png)

The card follows Windows app light/dark preference and high contrast. Its measured layout adapts to display DPI and Windows text size. These images show sample data.

## Requirements and running

- **`CodexUsageNotch.exe`** — portable, self-contained Windows x64 edition. Includes .NET; no separate runtime installation needed.
- **`CodexUsageNotch-lite.exe`** — smaller Windows x64 edition requiring the .NET 8 Windows Desktop Runtime.

Both editions require an interactive Windows desktop and an installed Codex executable. Release binaries are generated locally in `dist/`; they are not stored in Git. For a fresh checkout, build from source using the commands below or produce both editions with the release script. The project currently targets `net8.0-windows`, `win-x64`, version **1.2.2**.

Keep the executable in a stable folder if you enable startup. Run one edition at a time; a second launch exits quietly. The portable edition is larger because it bundles the runtime and WinForms.

Codex must be installed. The indicator looks for `codex.exe` beside the executable, in conventional Codex installation folders, on PATH, and in the ChatGPT VS Code/VS Code Insiders extension. It uses your existing Codex login.

When Codex explicitly reports that sign-in is required, the app offers browser sign-in once per launch. You can also select **Connect ChatGPT**. The sign-in window offers **Open sign-in** and **Copy sign-in link**; failures let you request a fresh link. Closing that window cancels this indicator's pending login. Sign-in attempts expire after five minutes.

## Controls

| Action | Result |
| --- | --- |
| Hover the tray icon | Show a usage preview without taking keyboard focus |
| Click the tray icon, or activate it with Enter/Space | Open an interactive card with Refresh and Connect ChatGPT |
| Move from the icon into the preview | Keep it open while reading |
| Leave the icon and preview | Close after a 400 ms grace period |
| Escape in the interactive card | Close and return to the notification area |
| Click elsewhere | Close the interactive card |
| Hover the top strip for about 850 ms | Show the same usage details |
| Right-click the tray icon | Refresh, connect, show/hide the strip, toggle the ring, configure startup, or exit |

The top strip is visible by default. Strip visibility and the optional tray usage ring are remembered. **Start with Windows** remains opt-in and uses a per-user registry entry without administrator access. Double-click no longer toggles the strip; use **Show top indicator** in the menu.

The card appears beside the notification icon, including the overflow tray, and is kept inside the monitor's working area. The strip stays centered at the top of the primary monitor. Hover previews stay out of Alt+Tab; keyboard focus is reserved for explicit interaction. Transitions are immediate, with no motion animations.

## Understanding usage

The large value is the remaining primary allowance. The card shows actual window durations supplied by Codex, the primary reset countdown, and the secondary remaining percentage and reset time when available.

- Mint: more than 25% remaining.
- Amber: 11–25% remaining.
- Red: 10% or less remaining.
- Muted with **LAST KNOWN**: retained data after a failed refresh or two minutes without a successful update.

Status text explains missing Codex, unavailable windows, sign-in, and retries. A past reset time displays **Awaiting reset update**: the app never guesses that an allowance has replenished. These are subscription allowance values, not API spend or token estimates.

## Connection and privacy

The indicator owns one local `codex app-server --listen stdio://` subprocess. It follows the [experimental Codex app-server interface](https://developers.openai.com/codex/app-server), receives usage notifications, and confirms usage once per minute. Countdown repainting does not send requests. Requests time out after 15 seconds; failed connections retry after 5, 15, 30, then 60 seconds.

The indicator does not read, copy, or store authentication files, API keys, usage history, or account identity. Authentication remains with Codex. The only persisted presentation data is `%LocalAppData%\CodexUsageNotch\settings.json`. Existing settings are preserved when upgrading.

Sanitized diagnostics are kept beside settings in `diagnostics.log` and `diagnostics.log.1`, approximately 128 KiB each. They contain timestamps, fixed operation names, exception types, and numeric error codes. They exclude raw protocol payloads, exception messages, sign-in URLs, and credentials. Nothing is uploaded by the indicator.

## Build and validate

Requires Windows x64 and .NET 8 SDK or newer. No third-party runtime or test-framework packages are used; initial restore may download Microsoft runtime/build packs.

```powershell
dotnet build -c Release
dotnet run --project tests\CodexUsageNotch.Tests.csproj -c Release
```

Start the app from source:

```powershell
dotnet run --project CodexUsageNotch.csproj -c Release
```

Run these commands from the repository root. The tests are a standalone console runner, so use `dotnet run` rather than `dotnet test`. A successful run prints its passed checks and returns exit code 0; failures return 1. The default suite uses simulated transports and does not require a Codex login.

Run desktop interaction checks from an interactive Windows session. They use sample data, briefly move the pointer, open a test card, and restore the pointer:

```powershell
dotnet run --project tests\CodexUsageNotch.Tests.csproj -c Release -- --ui
```

Optionally verify the installed, signed-in Codex connection with a single read-only request. This command never starts sign-in:

```powershell
dotnet run --project tests\CodexUsageNotch.Tests.csproj -c Release -- --live
```

Generate 324 sample renders with bounds/overlap assertions, covering light/dark/high-contrast palettes, 100/150/200% DPI, 100/200/225% text size, and twelve usage/error states:

```powershell
dotnet run --project CodexUsageNotch.csproj -c Release -- --render-previews .\build\previews
```

The output includes PNGs and `layout-checks.txt`. Rendering uses sample data and does not connect to Codex.

### Build portable releases

Double-click `build\build-release.bat`, or run:

```powershell
.\build\build-release.ps1 -StageOnly
# Exit the current indicator from its tray menu after staging succeeds.
.\build\build-release.ps1 -PromoteOnly
```

If PowerShell blocks unsigned scripts, use `build\build-release.bat` with the same switches. The batch wrappers for release and cleanup set the execution policy for their child process only; they do not change the machine policy.

The script runs core and desktop interaction tests, builds both editions under `build\staging`, and smoke-tests each executable against the actual Windows tray using sample data. Run it in an interactive desktop session; the desktop tests briefly move the pointer. Promotion checks their SHA-256 hashes, refuses to overwrite a running edition, and copies validated files to `dist` and the project root. The previous executables are retained with `.previous` suffixes for rollback. Omitting the switches runs both stages. `dist/release-manifest.json` records the version, validation time, and executable hashes.

To roll back, exit the app and replace the executable with its `.previous` copy. To remove the app, disable **Start with Windows**, exit, and delete its executable. Local settings/logs can be removed separately.

## Project layout and maintenance

| Path | Purpose |
| --- | --- |
| `Program.cs` | Entry point, per-user single-instance guard, preview and smoke modes |
| `NotchApplicationContext.cs` | Application lifecycle, refresh scheduling, login and tray actions |
| `Core/` | Usage state, parsing, presentation preferences, diagnostics and startup registration |
| `Integration/` | Codex executable discovery, app-server transport and native Windows tray host |
| `UI/` | Usage card, indicator windows, themes, tray icon and sample rendering |
| `Assets/` | Application icons and README screenshots using sample values |
| `tests/` | Console regression runner and Windows desktop interaction checks |
| `build/` | Release and cleanup scripts; ignored generated output |
| `VALIDATION.md` | Validation results and remaining manual checks |

To remove disposable build output, rendered previews, generated protocol-review schemas and smoke reports:

```powershell
.\build\clean.bat -WhatIf # Preview the cleanup
.\build\clean.bat
```

Cleanup preserves `dist/`, validated `build/staging/` releases, root executables and `.previous` rollback copies. It never changes application settings or startup registration. Exit development/test executables first if Windows reports locked files. Building and testing recreates `bin/` and `obj/`.

Git tracks source, scripts, documentation and source assets. Build output, release binaries, local IDE state, logs and environment overrides are ignored. `.gitattributes` normalizes source text and preserves Windows line endings for shell scripts. Review changes with `git status` and `git diff` before committing. No remote hosting or public release is configured by these scripts.

## Troubleshooting and validation limits

- **Codex not found:** install Codex or put its executable on PATH, then select Refresh. An npm-only `.cmd` launcher must expose the underlying Windows `codex.exe` on PATH.
- **Last known usage:** the previous reading is preserved and retries continue. Check Codex's own connection; Refresh triggers an immediate retry.
- **Sign-in unavailable:** select Connect ChatGPT again. Browser or clipboard errors remain visible in the sign-in window.
- **Cannot save settings/startup:** the app reports the failure; the chosen display setting still works for the session.
- **Tray icon hidden:** open Windows' notification overflow area. Windows controls whether an icon stays visible on the taskbar.
- **Future Codex incompatibility:** the app-server contract is experimental; protocol changes may require an app update.

See [VALIDATION.md](VALIDATION.md) for completed checks and remaining manual scenarios. This is a portable release; installer, signing, automatic updates, and public publishing are not included.
