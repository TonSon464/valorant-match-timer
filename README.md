# Valorant Match Timer

A small Windows timer for Valorant that watches the local game log, tracks match length, shows an optional in-game overlay, and warns you before your get-off time.

The main window behaves like a normal window and never pins above other apps. The overlay is separate and only appears over fullscreen games.

__(Works on other games too!)__

> [!CAUTION]
> **This app isn't signed yet**
>
> The app is expected to be flagged by Windows SmartScreen.
> Nonetheless the app is safe ([checked on VirusTotal](https://www.virustotal.com/gui/file/d3708d564082a48dcf4a48ec64cefe0c5ba5c512f3cb3a1a420556be20154c75?nocache=1)).
>
> *The app will be signed soon on later patches.*


## Features

- **Get-off countdown** - set a clock time, add 30 minutes or an hour, or clear it.
- **Automatic match detection** - reads `ShooterGame.log` to detect match start and end, with no game integration or network access.
- **Match history** - learns typical match lengths and predicts when the current match will end.
- **Manual timers** - Competitive, Unrated, Swiftplay, Spike Rush, Deathmatch, Team Deathmatch, Custom, and a custom duration field.
- **Optional overlay** - shows the get-off time, match info, predicted end, and clock; can be locked click-through and only shown while a fullscreen game is focused.
- **Shutdown warning** - a cancellable countdown window instead of an instant shutdown. If your cutoff arrives during a match, the app waits for the match and overtime to finish.
- **Start with Windows** - optional; managed through the current user's Run key.
- **Local only** - settings, statistics, and history stay on your PC.

## Download

Download the latest `ValorantMatchTimer.exe` from the [Releases](../../releases) page.

The executable is self-contained and does not require a separate .NET installation.

## Usage

1. Start `ValorantMatchTimer.exe`.
2. Set the time you need to get off, or use **+30m** / **+1h**.
3. Leave the app running. It watches the Valorant log and updates automatically.
4. Open the gear button for overlay, shutdown, and activity settings.

If the log is not detected automatically, set the path in **Settings > General**. The default location is:

```
%LOCALAPPDATA%\VALORANT\Saved\Logs\ShooterGame.log
```

## Settings and data

- Settings: `%APPDATA%\ValorantMatchTimer\settings.json`
- Match duration history: `%APPDATA%\ValorantMatchTimer\history.json`
- Startup entry: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ValorantMatchTimer`

Closing the main window exits the app.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" build ValorantMatchTimer.sln -c Debug
```

Publish a self-contained single-file executable:

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish src\ValorantMatchTimer\ValorantMatchTimer.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o dist
```

The result is `dist\ValorantMatchTimer.exe`.

## Legacy version

The original Python version is kept in [`legacy/`](legacy/) for reference and is no longer the primary application.

## License

MIT - see [LICENSE](LICENSE).
