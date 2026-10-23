# Releases and updates

`Directory.Build.props` is the version source for the app and installer. Increment `StransferVersion` and push to `main`. The Publish release workflow tests the app, builds full Windows x64 binaries, packages them and creates a GitHub Release. It skips a version that already has a release. Published versions should not be overwritten; use a new version for a correction.

Release assets:

- `Stransfer-Setup-win-x64.exe`: installer with embedded application payload.
- `Stransfer-portable-win-x64.zip`: one `Stransfer` folder containing only `Stransfer.exe` and `START-HERE.txt`.
- `update-win-x64.json`: version, sizes and SHA-256 hashes for the portable ZIP and executable.
- `SHA256SUMS.txt`: hashes for the downloadable files.

Build outputs, SDKs, test files and transfer state are excluded from release packages. Settings and job records stay in `%LOCALAPPDATA%/Stransfer`, outside the app folder.

## Download permission

Settings → Updates offers manual checks and an automatic-check toggle. Automatic checks fetch only public GitHub release metadata, initially after startup and then at most every six hours. They never download an application package. A new version is shown in Settings and announced through the tray.

Download and update opens a confirmation with the version, size and restart behavior. Only Allow download fetches the manifest and portable package. Downloads can be cancelled. Neither the GitHub connection used during development nor any developer token is embedded in the app.

## Applying an update

The updater checks destination write access and free space, downloads into an AppData staging directory, validates declared sizes and SHA-256 hashes, accepts only the expected ZIP paths, and verifies the executable version and processor architecture. The verified new executable runs in helper mode without a window. It waits for the running app to save progress and exit, copies the executable into the same app folder and keeps a rollback copy until the new app acknowledges startup.

No installer wizard, elevation or separate updater installation is used. Portable and installed full builds use the same update package. The updater does not alter shortcuts, integration choices, settings, job records or user files. It refuses linked app paths and unwritable folders; move a portable copy to a regular writable folder if needed.

The helper restores the prior executable if the replacement cannot start. Unexpected power loss or a disconnected portable drive during the replacement can still require manual recovery. Download the current portable build and replace only `Stransfer.exe`; saved state remains in AppData. Builds are not Authenticode-signed: Windows may show its normal reputation warning for a new download. SHA-256 protects download integrity; it is not a substitute for a publisher signing certificate.

## Validation

Update tests use mocked release metadata and temporary directories. They check metadata-only discovery, equal/older/prerelease rejection, manifest matching, SHA-256 failures, ZIP traversal refusal, replacement, rollback and retained settings. The WPF suite checks the real update page in an isolated profile without contacting GitHub. A published-executable lifecycle test covers helper startup, graceful parent shutdown, in-place replacement, restart acknowledgement and cleanup.
