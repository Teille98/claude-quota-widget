# Claude Quota Widget

See your **Claude subscription usage limits** at a glance — on the **Windows** desktop and on your **Android** home screen.

It shows the same numbers as *Settings → Usage* on claude.ai:

- **Session** — the rolling 5-hour limit,
- **Week** — the weekly limit across all models,
- **Week · model** — the weekly limit for a specific model, when your plan has one,

each with its percentage, a colour that changes at 50 % and 80 %, and when it resets.

> **Unofficial.** This project is not affiliated with or endorsed by Anthropic. It reads an **undocumented** claude.ai endpoint that can change or disappear at any time; if that happens, the apps say so ("unexpected format") instead of showing wrong numbers. Use it only with your own account.

## Features

### Windows (tray app)

- **Tray icon**: a ring filled to your highest usage, coloured by threshold; grey when the values are stale, with an amber dot when you need to sign in again.
- **Panel** (left click): every limit with a progress bar, reset time and "updated … ago", plus a **7-day history chart** (weekly and session usage).
- **Floating window** (optional): a small always-on-top overlay with the session and weekly limits. It never steals focus, can be dragged anywhere (snaps to screen edges, remembers its position), fades when idle, hides itself while a full-screen app is in front, and has a **compact** two-ring mode.
- **Notifications** when a limit crosses 80 % and when the claude.ai session expires.
- Refreshes every 5 minutes and after the PC wakes up (≈1.3 s per read).
- Optional start with Windows (from the menu or the installer).
- **JSON export** of the latest values for other tools (see [below](#json-export-windows)).

### Android (home-screen widget)

- **Resizable widget**: **2×1** (two rings), **4×2** (details with bars and reset times), **5×2** (three columns: session, week, week per model). Light and dark themes follow the system.
- **App screen**: the same panel, a **7-day history chart**, sign-in, "add widget to home screen", and a shortcut to allow background reads.
- Background refresh every ~15 minutes with WorkManager (the Android minimum), plus a refresh button on the widget.
- Notifications at 80 % and when the session expires.

### Both

- **English and French** interface, picked from the system language.
- Nothing is sent anywhere except to claude.ai, by the embedded browser itself.

## How it works

1. You sign in to claude.ai **once** in an embedded browser (WebView2 on Windows, Android System WebView). "Continue with Google" works.
2. To read the quota, the app loads a claude.ai page in a hidden web view and runs [`shared/usage-script.js`](shared/usage-script.js) **inside that page**. The script calls `/api/organizations` and `/api/organizations/{id}/usage` — the same requests the Usage settings page makes — and posts back only the list of limits.
3. The app parses that list, caches the last good values, and updates the tray icon or widget.

### Privacy and security

- **The app's code never reads, copies or stores cookies or tokens.** Requests are same-origin calls made by the web view, which attaches its own session cookies. The session lives only in the web view's profile, as in a browser.
- The only data the app stores is the quota values (latest values and a 7-day history of percentages) and your display preferences.
- Nothing sensitive is written to logs. No telemetry, no third-party servers.
- To sign out, remove the app's data (Windows: the uninstaller offers to; Android: *App info → Storage → Clear data*).

| Platform | Location | Contents |
|---|---|---|
| Windows | `%LOCALAPPDATA%\ClaudeQuotaWidget\` | `last.json` (cached values), `history.json` (7-day history), `quota.json` (export), `settings.json` (preferences), `WebView2\` (browser profile holding the claude.ai session) |
| Android | app-private storage | cached values, 7-day history, a log of the last 100 reads (time, result, duration — no personal data), the WebView profile |

## JSON export (Windows)

After every successful read, the Windows app writes `%LOCALAPPDATA%\ClaudeQuotaWidget\quota.json`, so scripts and other tools (a Claude Code status line, a dashboard…) can show the quota without talking to claude.ai themselves. The format is stable within a `schema` version:

```json
{
  "schema": 1,
  "source": "claude-quota-widget",
  "fetched_at": "2026-11-15T17:11:23Z",
  "session_percent": 38,
  "weekly_percent": 42,
  "limits": [
    { "kind": "session",       "label": "Session (5h)",       "model": null,    "percent": 38, "resets_at": "2026-11-15T19:50:00Z" },
    { "kind": "weekly_all",    "label": "Week · all models",  "model": null,    "percent": 42, "resets_at": "2026-11-16T19:00:00Z" },
    { "kind": "weekly_scoped", "label": "Week · Fable",       "model": "Fable", "percent": 2,  "resets_at": "2026-11-16T19:00:00Z" }
  ]
}
```

Times are UTC (ISO 8601). Check `fetched_at` to know how fresh the values are: the file is not updated while the PC is offline or signed out.

## Install

Download from the [Releases](../../releases) page:

- **Windows 10/11 (x64)**: `ClaudeQuota-Setup-x.y.z.exe`. Per-user install, no admin rights needed; .NET is bundled. The WebView2 runtime ships with Windows 11 (on Windows 10, install it from Microsoft if missing). The installer is not code-signed, so SmartScreen may ask you to confirm (*More info → Run anyway*).
- **Android 12+**: `ClaudeQuota-x.y.z.apk`. Allow installing from your browser or file manager when asked.

Then sign in to claude.ai when the app asks.

## Build from source

### Windows

Requirements: .NET 10 SDK; [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer.

```sh
cd win
dotnet test ClaudeQuota.Core.Tests            # unit tests
dotnet build ClaudeQuota.slnx -c Release      # app: ClaudeQuota.Win/bin/Release/net10.0-windows/ClaudeQuota.exe
powershell -File build-installer.ps1          # installer: ../dist/ClaudeQuota-Setup-<version>.exe
```

### Android

Requirements: JDK 17+ (the one bundled with Android Studio works), Android SDK 36.

```sh
cd android
./gradlew testDebugUnitTest assembleDebug     # debug build, installs next to the release app as "Claude Quota (dev)"
./gradlew assembleRelease                     # release APK
```

Release signing reads the keystore from Gradle properties, never from the repository. Put these in `~/.gradle/gradle.properties` (or `$GRADLE_USER_HOME/gradle.properties`):

```properties
CLAUDEQUOTA_STORE_FILE=/path/to/release.jks
CLAUDEQUOTA_STORE_PASSWORD=...
CLAUDEQUOTA_KEY_ALIAS=...
CLAUDEQUOTA_KEY_PASSWORD=...
```

Without them, `assembleRelease` produces an unsigned APK.

## Repository layout

| Path | What |
|---|---|
| `shared/usage-script.js` | The script run inside claude.ai, shared by both platforms |
| `win/ClaudeQuota.Core` | Platform-independent model: parsing, thresholds, text formatting, cache (unit-tested) |
| `win/ClaudeQuota.Win` | Windows app: WinForms + WebView2 (tray, panel, floating window, sign-in) |
| `win/installer` | Inno Setup script |
| `android/` | Android app and widget: Kotlin, RemoteViews, WorkManager |
| `bridge/` | Optional Claude Code status line (see below) |

### Porting to another platform

The logic is small. A port needs an embedded web view with a persistent profile for the sign-in, a way to run `shared/usage-script.js` in a claude.ai page and receive its JSON message, and a parser equivalent to `UsageParser` (`win/ClaudeQuota.Core/Quota.cs` or `android/app/src/main/java/dev/teo/claudequota/Quota.kt`). Their unit tests double as a specification of the message format. On Apple platforms, note that widgets cannot run a web view: the app has to do the reads and share the values with the widget (App Group).

## Claude Code status line (optional)

`bridge/statusline.mjs` (Node.js ≥ 18) is a [status line](https://code.claude.com/docs/en/statusline) for Claude Code that shows the model, context usage and the 5-hour / 7-day limits reported by Claude Code itself, and saves them to `data/quota.json`. It is independent of the apps. In `~/.claude/settings.json`:

```json
"statusLine": {
  "type": "command",
  "command": "node \"/path/to/claude-quota-widget/bridge/statusline.mjs\"",
  "refreshInterval": 60
}
```

Tests: `node --test bridge/statusline.test.mjs`.

## License

[MIT](LICENSE)
