# Architecture

## Boundaries

| Layer | Responsibility |
|---|---|
| `Stransfer.Core` | Planning, capacity checks, bounded copying, verification, volume identity and durable recovery |
| `App/Models` | Settings schema and atomic local persistence |
| `App/Services` | Queue scheduling, tray lifecycle, dialogs, startup/menu registration, keyboard integration, IPC and optional tools |
| `App/ViewModels` | Observable UI state and commands; no XAML layout |
| `App/Views` | Separate Transfers, Settings, Tools and Shortcuts XAML layouts |
| `App/Themes` | Shared tokens and control templates; dynamic resource references |

The application uses WPF with no third-party UI or transfer package. `MainWindow` delegates application lifecycle to `AppRuntime`. The latter composes services and view models. The core does not depend on the application.

## Transfer lifecycle

Planning is serialized so pending jobs cannot claim the same top-level destination names. Sources are normalized, overlapping selections pruned and folder recursion rejected. The plan records volume identity, source length/mtime and destination paths. A preflight checks allocation-rounded remaining space, pending jobs, headroom, FAT32 limits and write access before persisting the accepted job. Execution checks space again.

Copying uses a separate partial file for each plan entry. Large-file buffers are pooled and a read-ahead operation overlaps the current write. Small-file parallelism is bounded; unknown/HDD storage remains sequential by default. A native Windows directory guard pins validated ancestors without delete sharing, allowing checks to be reused during execution. Handles are released on every terminal path.

Data is flushed before its durable offset is journaled. Ready-to-publish intent is flushed before renaming a partial file. Parallel files group journal entries into a single flush; each waiter resumes only after that flush. The main plan is periodically compacted with a new epoch. A torn journal tail is truncated on recovery. Offsets are reset to recorded durable offsets, and resumed prefixes are compared to the source.

Completion entries are appended without a separate disk barrier: the earlier durable intent and flushed file data already allow recovery if a completion entry is lost. The next intent flush, graceful pause or final job-status flush makes preceding completion entries durable too. Journal streams avoid combining write-through writes with an additional explicit disk flush; the explicit flush is the durability boundary.

Copy verification computes a source SHA-256 inline and rereads the destination. Cross-volume move deletion requires matching source and destination hashes under read locks. Same-volume Windows moves record the source's stable file ID and rename its exact open handle, preserving the original file. Recovery validates that identity before accepting a native move. Short sharing/lock violations retry with bounded backoff and full revalidation.

## Explorer and background behavior

Per-user registry settings add explicit Explorer commands and optional sign-in startup. No administrator account is needed for those entries. Single-instance commands use a per-user/session mutex and a current-user-only named pipe with bounded messages.

The opt-in shortcut service uses `RegisterHotKey` for Ctrl+Shift+V with `MOD_NOREPEAT` and handles `WM_HOTKEY` on the main window, including while it is hidden. It never registers or hooks Ctrl+V or Shift+Insert. Registration conflicts appear on the Shortcuts page with a Retry command. The service reads the clipboard and resolves Explorer only on demand, eliminating repeated idle COM/clipboard polling.

`IShellBrowser.QueryActiveShellView`, visible view HWNDs and `IFolderView.GetFolder` identify a unique active folder, including toolbar/address-bar focus. The destination is captured before reading a delayed device clipboard. Focus and clipboard sequence are checked before accepting it. An uncertain target opens a folder picker and starts on selection; a changed clipboard rejects the request. A preparation guard prevents duplicate starts. Resume prompts and open dialogs block shortcut acceptance. Native context Paste and drag/drop keep Windows behavior. Full exit unregisters the hotkey.

USB phones and other Shell namespace selections expose `Shell IDList Array` instead of ordinary file paths. The clipboard reader retains their `IDataObject`, marshals it to a dedicated STA and calls `IFileOperation.CopyItems`. Typed COM interfaces avoid passing an IUnknown vtable where IShellItem is required. Windows' device provider supplies transfer progress, capacity/error handling and cancellation; a progress sink forwards status and exit cancellation. This copy-only route keeps device originals and uses keep-both names. It does not claim the core's durable checkpointing or verification. Descriptor-only clipboard objects are rejected. No temporary full-file staging copy is created.

The scheduler runs one job at a time. Snapshots are cached off the UI; a 500 ms UI refresh does not acquire filesystem-backed state locks. Reconnect checks run off the UI every three seconds. An interrupted job is prompted once while available; disconnecting resets the prompt opportunity. Exit cancels queued/running work, awaits stopped workers and disposes the tray and hotkey registration.

## Scope

Metadata and filesystem limits are explicitly listed in README. Preflight cannot reserve capacity against other programs. Checkpoints rely on filesystem/device flush behavior. Physical-disconnect, power-loss and Explorer tab/shortcut tests require target Windows hardware.

## References

- [Microsoft: querying the current Explorer shell view](https://devblogs.microsoft.com/oldnewthing/20040720-00/?p=38393)
- [Microsoft: copy hook handlers cover folders and printers](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-create-copy-hook-handlers)
- [Microsoft: IFileOperationProgressSink monitors an advised operation](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifileoperationprogresssink)
- [Microsoft: Windows Package Manager install options](https://learn.microsoft.com/en-us/windows/package-manager/winget/install)
