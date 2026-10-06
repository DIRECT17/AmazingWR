# AmzWR

AmzWR — Windows companion client for Amazing RP. It watches the selected `chatlog.txt` and displays concise notifications, event history, and a shared phone book. The dashboard also includes a small “Catch the cat” game.

## Download

Download the latest `AmazingWR.exe` from the [Releases](../../releases) page. The application targets Windows x64 and is published as a self-contained executable.

## Build from source

Requirements: Windows, PowerShell, and the .NET 10 SDK.

```powershell
./scripts/build-app.ps1
```

The build output is created under `dist/`.

## Privacy

The client reads the game log path selected by the user. For sign-in, licensing, and usage metrics, it communicates with the AmzWR service; the in-app terms explain which account and device information is sent. Do not put credentials or private service configuration in this repository.

## Release contents

The public repository contains the Windows client source and its build assets. The Telegram bot, server database, deployment configuration, and credentials are not part of this repository.
