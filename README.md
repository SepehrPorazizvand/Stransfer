# Stransfer

Windows file transfers with durable progress, pause/resume, destination preflight and optional Explorer integration. The application UI and documentation are in English.

## Use

Launch `dist/Stransfer/Stransfer.exe` for the portable full build, or run `dist/StransferSetup.exe` to install it. No .NET installation or external copy tool is needed. Portable profiles start with Explorer integration and sign-in startup disabled. Setup offers the integration options explicitly. Configure the keyboard shortcut on **Shortcuts** and background/startup behavior in **Settings**.

- **Ctrl+Shift+V:** copy or cut files, focus a destination folder in Explorer, then paste with Stransfer. Transfer starts directly without opening the main window. The Shortcuts page enables the shortcut, reports registration/conflicts and offers Retry. If the destination is uncertain, a folder picker starts the transfer as soon as you choose a folder. Keep the app running in the tray to use the shortcut.
- **Ctrl+V:** uses Windows default paste. Shift+Insert also retains its normal behavior.
- **Explorer menu:** enable “Paste with Stransfer,” then right-click a folder or its background. On Windows 11 use **Show more options**. This command starts or contacts the running app.
- **Background:** optionally keep the app in the tray and start it when signing in. The tray menu opens the queue, pauses transfers, or saves progress and exits.
- **Resume:** interrupted jobs are retained. On startup or after the original drives reconnect, Stransfer asks before resuming. Drive identity is checked even if the drive letter changed.
- **Transfer window:** add, drop or paste sources; select a destination; choose Copy/Move; click **Check and transfer**. Existing destinations are kept with numbered names. Pause, Resume, Open destination, Remove and completed History are available.
- **USB phones:** copy files or folders from the phone in Explorer, open a PC destination folder and press Ctrl+Shift+V. Shell namespace selections (`Shell IDList Array`) use Windows' device provider and its progress/cancel dialog without staging an extra copy on the PC. This device path always copies, keeping phone originals. Device transfers have Windows-managed progress/space checks; they are outside Stransfer's durable queue, SHA-256 verification and restart/resume system. After a disconnect, cancellation or restart, copy/paste the phone selection again. Descriptor-only attachments and phone-to-phone/PC-to-phone destinations are not supported by this path.

The built-in Explorer Paste menu, Explorer drag-and-drop, elevated windows and transfers initiated by other applications retain their behavior. Ctrl+Shift+V uses Windows hotkey registration without code injection or a keyboard hook. See [validation](docs/VALIDATION.md) for the tested scope and remaining hardware checks.

## Safety and capacity

Before accepting a job, Stransfer scans the files, validates sources, rejects unsafe links, checks filesystem limits and write access, estimates allocation units and directory overhead, and adds free-space headroom. Pending transfers reserve names and are counted against available capacity. Space is rechecked before starting or resuming.

This is a capacity estimate, not an operating-system reservation. Another application, a quota change or a failing/disconnected drive can still stop a transfer. A stopped job retains recoverable progress.

Data is written into job-specific partial files. The journal only records durable offsets after a disk flush. Resume compares the saved prefix against the source. SHA-256 copy verification is enabled by default; disabling it skips the destination readback. Cross-volume moves verify both files before deleting the source. Same-volume moves rename the original file atomically where supported.

Current limits:

- Keep-both is the supported conflict policy; Replace and Skip are not implemented.
- Symbolic links, junctions, EFS-encrypted streaming copies and named-stream streaming copies are rejected. Same-volume native file moves preserve named streams. Directory streams are rejected.
- Ordinary streamed copies preserve content, creation/modification times and basic read-only/hidden/system/archive/indexing attributes. They do not clone ACLs, hard-link relationships, sparse allocation, compression settings or every Windows metadata feature.
- Source files changed while stopped are rejected. Open/locked files can require closing the application using them. Short-lived sharing violations receive bounded retries.
- Recovery depends on keeping the job records and `.stransfer-*.partial` files. Hardware that ignores flushes, filesystem damage or physical failure cannot be repaired by a copy utility.

## Performance

- Auto uses four workers for files up to 1 MiB only when both local drives report no seek penalty. HDDs, removable drives and uncertain devices use a sequential worker. Manual choices are 1/2/4/8 workers.
- Large files use bounded pooled buffers with overlapping read/write. Source hashing runs inline.
- Small-file workers share durable journal flushes. The immutable file plan is not rewritten for every file.
- Validated Windows directories are pinned without delete sharing, reducing repeated ancestor checks while protecting their paths during the run.
- Only one queued job runs at a time to avoid competing disk workloads.

These choices do not guarantee faster transfers on every device. Tiny-file filesystem overhead remains. Recovery and verification add work. The [performance report](docs/PERFORMANCE.md) compares a native `File.Copy` plus flush baseline with Stransfer on this development machine; it is not a benchmark of Explorer's UI or USB/HDD hardware. Both wins and regressions are retained in the results.

```powershell
dotnet run --project tests/Stransfer.Benchmarks -c Release -- benchmark.json
dotnet run --project tests/Stransfer.Benchmarks -c Release -- benchmark-verified.json --verify
```

## Optional tools

Transfers need no optional packages. The Tools page explains and individually installs 7-Zip (separate archive utility) or the .NET 10 SDK (source builds/tests). Installed tools show **Remove**; missing tools show **Install**. Status refreshes when the page/window is opened and with **Refresh installed tools**, including SDK versions. Remove targets that exact package through winget; other installed versions may remain. Install needs a connection, may require elevation, and accepts the package/source agreements only after the user clicks Install. Tools are not bundled or installed automatically. Official website links are available if winget is missing.

The **slim build requires the .NET 10 Windows Desktop Runtime before it can launch**. The full build includes that runtime. A runtime required to launch the UI cannot be installed from inside an app that has not started.

## Build and package

Requires Windows and the .NET 10 SDK. No NuGet package dependencies.

```powershell
./scripts/build.ps1                     # Tests + small framework-dependent build
./scripts/build.ps1 -SelfContained      # Tests + full standalone build
./scripts/build.ps1 -Runtime win-arm64  # Build for ARM64; test on the target hardware
./scripts/install.ps1                   # Per-user full install
./scripts/install.ps1 -Slim             # Per-user slim install; Desktop Runtime required
./scripts/package-source.ps1            # Small GitHub-ready source ZIP
./scripts/uninstall.ps1                 # Remove installation/integration; retain job records
```

Full output: `dist/Stransfer/`. Slim output: `dist/Stransfer.Slim/`. Source ZIP: `dist/Stransfer-source.zip`. `.gitignore` excludes SDK downloads, build outputs, runtime bundles and local test data. Do not commit `.tools/` or `dist/`. No repository is published automatically.

Settings and job records are local at `%LOCALAPPDATA%/Stransfer/`. `STRANSFER_STATE_DIR` selects an isolated test profile and disables registry changes and global shortcut registration.

## UI and code

Change shared colors, fonts, spacing and control styling in `src/Stransfer.App/Themes/`. Page layouts are separate XAML files in `Views/`; view models contain UI state/commands, and services own operating-system behavior. `MainWindow.xaml.cs` only handles window-level UI events. Transfer code lives in `Stransfer.Core`, independent of WPF. See [architecture](docs/ARCHITECTURE.md), [tests](tests/README.md), and the [logo brief](docs/LOGO-PROMPT.md).
