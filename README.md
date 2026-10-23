<h1 align="center">Stransfer</h1>
<p align="center">Windows file transfers with pause, resume and saved progress.</p>
<p align="center">
  <img src="docs/media/transfer-demo.gif" alt="Stransfer transfer animation" width="900">
</p>

## Download

- **[Installer (.exe)](https://github.com/SepehrPorazizvand/Stransfer/releases/latest/download/Stransfer-Setup-win-x64.exe)** — install Stransfer on Windows.
- **[Portable (.zip)](https://github.com/SepehrPorazizvand/Stransfer/releases/latest/download/Stransfer-portable-win-x64.zip)** — extract the folder and run `Stransfer.exe`.

Windows x64. Both downloads include the runtime; no extra copy tools are needed.

## Use

1. Copy or cut files in File Explorer.
2. Open the destination folder.
3. Press **Ctrl+Shift+V** for Stransfer. **Ctrl+V** uses Windows paste.

The compact progress window shows speed, remaining time and Pause/Resume. Keep Stransfer running in the tray to use its shortcut. Configure the shortcut on **Shortcuts**.

## Features

- Copy and move files between local drives, USB drives and supported USB phones.
- Pause transfers and resume saved progress after restarting or reconnecting a drive.
- Check destination space before starting.
- Choose Fast mode or SHA-256 verification in Settings.
- Keep settings and transfer history between updates.

Phone transfers require an unlocked phone in File transfer (MTP) mode. Some devices restart the current incomplete file when resuming. Speed depends on the devices and workload.

## Updates

Open **Settings → Updates → Check for updates**. Stransfer asks before downloading. After approval, it verifies the files, saves active transfer progress, updates in the same folder and restarts without a setup wizard.

Automatic checks can be disabled. Downloads never start automatically.

## Build

Windows and the .NET 10 SDK are required.

```powershell
./scripts/build.ps1 -SelfContained
./scripts/package-release.ps1 -SkipBuild
```

Release files are in `dist/releases/`. Change `StransferVersion` in `Directory.Build.props` and push to `main` to publish a newer release automatically after tests pass.

[Safety and test status](docs/VALIDATION.md) · [Performance](docs/PERFORMANCE.md) · [Release and update details](docs/UPDATES.md)
